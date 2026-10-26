using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Delivery;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class PostgresTelegramOutboxDeliveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task DeliverBatchAsync_TransientFailureAndRestart_PreservesChatOrderWithoutBlockingOtherChats(bool sameTimestamp)
    {
        var connection = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required for this test.");
        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new OwnDayDbContext(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connection).Options);
#pragma warning disable EF1003 // Schema name is generated from a GUID.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003
        try
        {
            var options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(
                new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema }.ConnectionString).Options;
            var clock = new Clock();
            var sender = new FailOnceSender();
            await using (var db = new OwnDayDbContext(options))
            {
                await db.Database.MigrateAsync();
                var createdAt = clock.UtcNow.AddMinutes(-3);
                var deferred = Message(Guid.Parse("00000000-0000-0000-0000-000000000002"), 10, "Deferred",
                    sameTimestamp ? createdAt : createdAt.AddSeconds(30));
                deferred.NextAttemptAt = clock.UtcNow.AddSeconds(4);
                db.TelegramOutboxMessages.AddRange(
                    Message(Guid.Parse("00000000-0000-0000-0000-000000000001"), 10, "First", createdAt),
                    deferred,
                    Message(Guid.Parse("00000000-0000-0000-0000-000000000003"), 10, "Second", sameTimestamp ? createdAt : createdAt.AddMinutes(1)),
                    Message(Guid.Parse("00000000-0000-0000-0000-000000000004"), 20, "Other chat", createdAt.AddMinutes(2)));
                await db.SaveChangesAsync();

                await new TelegramOutboxDeliveryService(db, sender, NullLogger<TelegramOutboxDeliveryService>.Instance, clock)
                    .DeliverBatchAsync(CancellationToken.None);
                Assert.Equal(new[] { "First", "Other chat" }, sender.Attempts);
            }
            await using (var restarted = new OwnDayDbContext(options))
            {
                var delivery = new TelegramOutboxDeliveryService(restarted, sender,
                    NullLogger<TelegramOutboxDeliveryService>.Instance, clock);
                await delivery.DeliverBatchAsync(CancellationToken.None);
                Assert.Equal(2, sender.Attempts.Count);

                clock.UtcNow = clock.UtcNow.AddSeconds(2);
                await delivery.DeliverBatchAsync(CancellationToken.None);
                Assert.Equal(new[] { "First", "Other chat", "First" }, sender.Attempts);

                clock.UtcNow = clock.UtcNow.AddSeconds(2);
                await delivery.DeliverBatchAsync(CancellationToken.None);
                Assert.Equal(new[] { "First", "Other chat", "First", "Deferred", "Second" }, sender.Attempts);
                Assert.Equal(4, await restarted.TelegramOutboxMessages.CountAsync(message => message.Status == OutboxMessageStatus.Sent));
            }
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    private static TelegramOutboxMessage Message(Guid id, long chatId, string text, DateTime createdAt) => new()
    {
        Id = id, ChatId = chatId, Text = text, CreatedAt = createdAt,
        NextAttemptAt = createdAt, Status = OutboxMessageStatus.Pending
    };

    private sealed class Clock : TimeProvider
    {
        public DateTime UtcNow { get; set; } = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }

    private sealed class FailOnceSender : ITelegramMessageSender
    {
        public List<string> Attempts { get; } = [];

        public Task SendMessageAsync(long chatId, string text, string? replyMarkupJson, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.Add(text);
            return Attempts.Count == 1 ? Task.FromException(new HttpRequestException("Temporary failure")) : Task.CompletedTask;
        }
    }
}
