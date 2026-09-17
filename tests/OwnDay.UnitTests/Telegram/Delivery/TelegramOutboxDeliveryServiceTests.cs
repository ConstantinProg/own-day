using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Delivery;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Delivery;

public sealed class TelegramOutboxDeliveryServiceTests
{
    [Fact]
    public async Task DeliverPendingAsync_SendSucceeds_MarksMessageSent()
    {
        await using var fixture = await DeliveryFixture.CreateAsync(new RecordingMessageSender());
        var message = fixture.AddPendingMessage();
        await fixture.DbContext.SaveChangesAsync();

        await fixture.DeliveryService.DeliverPendingAsync(CancellationToken.None);

        Assert.Equal(OutboxMessageStatus.Sent, message.Status);
        Assert.NotNull(message.SentAt);
        Assert.Equal(0, message.AttemptCount);
    }

    [Fact]
    public async Task DeliverPendingAsync_SendFails_SchedulesRetry()
    {
        await using var fixture = await DeliveryFixture.CreateAsync(new FailingMessageSender());
        var message = fixture.AddPendingMessage();
        await fixture.DbContext.SaveChangesAsync();

        await fixture.DeliveryService.DeliverPendingAsync(CancellationToken.None);

        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        Assert.Equal(1, message.AttemptCount);
        Assert.NotNull(message.LastError);
        Assert.True(message.NextAttemptAt > DateTime.UtcNow.AddSeconds(-1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliverPendingAsync_SecondSendCancelled_PersistsFirstAttempt(bool firstSendFails)
    {
        using var cancellation = new CancellationTokenSource();
        var sender = new CancellingMessageSender(cancellation, firstSendFails);
        await using var fixture = await DeliveryFixture.CreateAsync(sender);
        var first = fixture.AddPendingMessage(DateTime.UtcNow.AddMinutes(-2));
        var second = fixture.AddPendingMessage(DateTime.UtcNow.AddMinutes(-1));
        var originalNextAttemptAt = first.NextAttemptAt;
        await fixture.DbContext.SaveChangesAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.DeliveryService.DeliverPendingAsync(cancellation.Token));

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
                    NullLogger<TelegramOutboxDeliveryService>.Instance));
        }

        public TelegramOutboxMessage AddPendingMessage(DateTime? createdAt = null)
        {
            var message = new TelegramOutboxMessage
            {
                Id = Guid.NewGuid(),
                ChatId = 1,
                Text = "pong",
                Status = OutboxMessageStatus.Pending,
                NextAttemptAt = DateTime.UtcNow.AddMinutes(-1),
                CreatedAt = createdAt ?? DateTime.UtcNow.AddMinutes(-1)
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
        public Task SendTextMessageAsync(
            long chatId,
            string text,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CancellingMessageSender(
        CancellationTokenSource cancellation,
        bool firstSendFails) : ITelegramMessageSender
    {
        public List<CancellationToken> Tokens { get; } = [];

        public Task SendTextMessageAsync(
            long chatId,
            string text,
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
        public Task SendTextMessageAsync(
            long chatId,
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Telegram is unavailable."));
    }
}
