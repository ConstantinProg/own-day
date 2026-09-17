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

        public TelegramOutboxMessage AddPendingMessage()
        {
            var message = new TelegramOutboxMessage
            {
                Id = Guid.NewGuid(),
                ChatId = 1,
                Text = "pong",
                Status = OutboxMessageStatus.Pending,
                NextAttemptAt = DateTime.UtcNow.AddMinutes(-1),
                CreatedAt = DateTime.UtcNow.AddMinutes(-1)
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

    private sealed class FailingMessageSender : ITelegramMessageSender
    {
        public Task SendTextMessageAsync(
            long chatId,
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Telegram is unavailable."));
    }
}
