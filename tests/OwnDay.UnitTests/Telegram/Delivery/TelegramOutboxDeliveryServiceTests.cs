using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Delivery;
using Telegram.Bot.Exceptions;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Delivery;

public sealed class TelegramOutboxDeliveryServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(11, true)]
    public async Task DeliverBatchAsync_ReadyMessages_ReturnsWhetherBatchWasFull(
        int messageCount, bool expectedFullBatch)
    {
        var sender = new RecordingMessageSender();
        await using var fixture = await DeliveryFixture.CreateAsync(sender);
        var expected = new List<SentMessage>();
        for (var index = 0; index < messageCount; index++)
        {
            var message = fixture.AddPendingMessage(Now.AddMinutes(index - messageCount), 100 + index, $"Reply {index}");
            if (index < 10)
            {
                expected.Add(new SentMessage(message.ChatId, message.Text, CancellationToken.None));
            }
        }

        fixture.AddPendingMessage().Status = OutboxMessageStatus.Sent;
        fixture.AddPendingMessage().Status = OutboxMessageStatus.Failed;
        var futureMessage = fixture.AddPendingMessage();
        futureMessage.NextAttemptAt = Now.AddDays(1);
        await fixture.DbContext.SaveChangesAsync();

        var fullBatch = await fixture.DeliveryService.DeliverBatchAsync(CancellationToken.None);

        Assert.Equal(expectedFullBatch, fullBatch.IsFullBatch);
        Assert.Equal(expected, sender.Messages);
        Assert.Equal(1 + Math.Min(messageCount, 10), await fixture.DbContext.TelegramOutboxMessages
            .CountAsync(message => message.Status == OutboxMessageStatus.Sent));
        Assert.Equal(OutboxMessageStatus.Pending, futureMessage.Status);
    }

    [Fact]
    public async Task DeliverBatchAsync_SendSucceeds_MarksMessageSent()
    {
        var sender = new RecordingMessageSender();
        await using var fixture = await DeliveryFixture.CreateAsync(sender);
        var message = fixture.AddPendingMessage(chatId: 123456789, text: "Queued reply");
        message.LastError = "Previous attempt failed";
        await fixture.DbContext.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();

        await fixture.DeliveryService.DeliverBatchAsync(cancellation.Token);

        Assert.Equal(new SentMessage(123456789, "Queued reply", cancellation.Token), Assert.Single(sender.Messages));
        fixture.DbContext.ChangeTracker.Clear();
        var stored = await fixture.DbContext.TelegramOutboxMessages.SingleAsync();
        Assert.Equal(OutboxMessageStatus.Sent, stored.Status);
        Assert.Equal(Now, stored.SentAt);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Null(stored.LastError);
    }

    [Fact]
    public async Task DeliverBatchAsync_SendFails_SchedulesRetry()
    {
        await using var fixture = await DeliveryFixture.CreateAsync(new FailingMessageSender());
        var message = fixture.AddPendingMessage();
        await fixture.DbContext.SaveChangesAsync();

        await fixture.DeliveryService.DeliverBatchAsync(CancellationToken.None);

        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        Assert.Equal(1, message.AttemptCount);
        Assert.NotNull(message.LastError);
        Assert.Equal(Now.AddSeconds(2), message.NextAttemptAt);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    public async Task DeliverBatchAsync_TransientTelegramFailureAfterFiveAttempts_RemainsPending(
        int errorCode)
    {
        await using var fixture = await DeliveryFixture.CreateAsync(
            new ErrorMessageSender(new ApiRequestException("Try again", errorCode)));
        var message = fixture.AddPendingMessage();
        message.AttemptCount = 4;
        await fixture.DbContext.SaveChangesAsync();

        await fixture.DeliveryService.DeliverBatchAsync(CancellationToken.None);

        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        Assert.Equal(5, message.AttemptCount);
        Assert.Equal(Now.AddSeconds(32), message.NextAttemptAt);
    }

    [Fact]
    public async Task DeliverBatchAsync_RepeatedTransientFailure_CapsRetryDelay()
    {
        await using var fixture = await DeliveryFixture.CreateAsync(new FailingMessageSender());
        var message = fixture.AddPendingMessage();
        message.AttemptCount = 12;
        await fixture.DbContext.SaveChangesAsync();

        await fixture.DeliveryService.DeliverBatchAsync(CancellationToken.None);

        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        Assert.Equal(Now.AddHours(1), message.NextAttemptAt);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task DeliverBatchAsync_PermanentTelegramError_MarksMessageFailed(int errorCode)
    {
        await using var fixture = await DeliveryFixture.CreateAsync(
            new ErrorMessageSender(new ApiRequestException("Rejected", errorCode)));
        var message = fixture.AddPendingMessage();
        await fixture.DbContext.SaveChangesAsync();

        await fixture.DeliveryService.DeliverBatchAsync(CancellationToken.None);

        Assert.Equal(OutboxMessageStatus.Failed, message.Status);
        Assert.Equal(1, message.AttemptCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliverBatchAsync_SecondSendCancelled_PersistsFirstAttempt(bool firstSendFails)
    {
        using var cancellation = new CancellationTokenSource();
        var sender = new CancellingMessageSender(cancellation, firstSendFails);
        await using var fixture = await DeliveryFixture.CreateAsync(sender);
        var first = fixture.AddPendingMessage(Now.AddMinutes(-2));
        var second = fixture.AddPendingMessage(Now.AddMinutes(-1));
        var originalNextAttemptAt = first.NextAttemptAt;
        await fixture.DbContext.SaveChangesAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.DeliveryService.DeliverBatchAsync(cancellation.Token));

        Assert.Equal(2, sender.Tokens.Count);
        Assert.All(sender.Tokens, token => Assert.Equal(cancellation.Token, token));

        fixture.DbContext.ChangeTracker.Clear();
        var storedFirst = await fixture.DbContext.TelegramOutboxMessages.SingleAsync(
            message => message.Id == first.Id);
        var storedSecond = await fixture.DbContext.TelegramOutboxMessages.SingleAsync(
            message => message.Id == second.Id);

        Assert.Equal(
            firstSendFails ? OutboxMessageStatus.Pending : OutboxMessageStatus.Sent,
            storedFirst.Status);
        Assert.Equal(firstSendFails ? 1 : 0, storedFirst.AttemptCount);
        if (firstSendFails)
        {
            Assert.Null(storedFirst.SentAt);
            Assert.Equal("Telegram is unavailable.", storedFirst.LastError);
            Assert.True(storedFirst.NextAttemptAt > originalNextAttemptAt);
        }
        else
        {
            Assert.NotNull(storedFirst.SentAt);
            Assert.Null(storedFirst.LastError);
        }

        Assert.Equal(OutboxMessageStatus.Pending, storedSecond.Status);
        Assert.Equal(0, storedSecond.AttemptCount);
        Assert.Null(storedSecond.SentAt);
        Assert.Null(storedSecond.LastError);
    }

    private sealed class DeliveryFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private DeliveryFixture(
            SqliteConnection connection,
            OwnDayDbContext dbContext,
            TelegramOutboxDeliveryService deliveryService)
        {
            _connection = connection;
            DbContext = dbContext;
            DeliveryService = deliveryService;
        }

        public OwnDayDbContext DbContext { get; }

        public TelegramOutboxDeliveryService DeliveryService { get; }

        public static async Task<DeliveryFixture> CreateAsync(ITelegramMessageSender sender)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var dbContext = new OwnDayDbContext(
                new DbContextOptionsBuilder<OwnDayDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await dbContext.Database.EnsureCreatedAsync();

            return new DeliveryFixture(
                connection,
                dbContext,
                new TelegramOutboxDeliveryService(
                    dbContext,
                    sender,
                    NullLogger<TelegramOutboxDeliveryService>.Instance,
                    new FixedTimeProvider(Now)));
        }

        public TelegramOutboxMessage AddPendingMessage(DateTime? createdAt = null, long chatId = 1, string text = "pong")
        {
            var message = new TelegramOutboxMessage
            {
                Id = Guid.NewGuid(),
                ChatId = chatId,
                Text = text,
                Status = OutboxMessageStatus.Pending,
                NextAttemptAt = Now.AddMinutes(-1),
                CreatedAt = createdAt ?? Now.AddMinutes(-1)
            };

            DbContext.TelegramOutboxMessages.Add(message);
            return message;
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingMessageSender : ITelegramMessageSender
    {
        public List<SentMessage> Messages { get; } = [];

        public Task SendMessageAsync(
            long chatId,
            string text,
            string? replyMarkupJson,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(new SentMessage(chatId, text, cancellationToken));
            return Task.CompletedTask;
        }
    }

    private sealed record SentMessage(long ChatId, string Text, CancellationToken Token);

    private sealed class CancellingMessageSender(
        CancellationTokenSource cancellation,
        bool firstSendFails) : ITelegramMessageSender
    {
        public List<CancellationToken> Tokens { get; } = [];

        public Task SendMessageAsync(
            long chatId,
            string text,
            string? replyMarkupJson,
            CancellationToken cancellationToken = default)
        {
            Tokens.Add(cancellationToken);
            if (Tokens.Count == 2)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return firstSendFails
                ? Task.FromException(new InvalidOperationException("Telegram is unavailable."))
                : Task.CompletedTask;
        }
    }

    private sealed class FailingMessageSender : ITelegramMessageSender
    {
        public Task SendMessageAsync(
            long chatId,
            string text,
            string? replyMarkupJson,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Telegram is unavailable."));
    }

    private sealed class ErrorMessageSender(Exception exception) : ITelegramMessageSender
    {
        public Task SendMessageAsync(
            long chatId,
            string text,
            string? replyMarkupJson,
            CancellationToken cancellationToken = default) => Task.FromException(exception);
    }
}
