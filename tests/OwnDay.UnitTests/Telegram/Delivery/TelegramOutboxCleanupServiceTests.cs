using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Delivery;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Delivery;

public sealed class TelegramOutboxCleanupServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task DeleteExpiredBatchAsync_MixedMessages_DeletesOnlyExpiredSentMessages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        var expired = CreateMessage(OutboxMessageStatus.Sent, Now.AddDays(-31));
        var retained = new[]
        {
            CreateMessage(OutboxMessageStatus.Sent, Now.AddDays(-30)),
            CreateMessage(OutboxMessageStatus.Sent, Now.AddDays(-1)),
            CreateMessage(OutboxMessageStatus.Sent, null),
            CreateMessage(OutboxMessageStatus.Pending, Now.AddDays(-31)),
            CreateMessage(OutboxMessageStatus.Failed, Now.AddDays(-31))
        };
        context.TelegramOutboxMessages.Add(expired);
        context.TelegramOutboxMessages.AddRange(retained);
        context.ProcessedTelegramUpdates.Add(new ProcessedTelegramUpdate
        {
            UpdateId = 1,
            ReceivedAt = Now.AddDays(-100),
            ProcessedAt = Now.AddDays(-100)
        });
        await context.SaveChangesAsync();

        var fullBatch = await new TelegramOutboxCleanupService(context, new FixedTimeProvider(Now))
            .DeleteExpiredBatchAsync(CancellationToken.None);

        Assert.False(fullBatch.IsFullBatch);
        var remaining = await context.TelegramOutboxMessages.AsNoTracking().ToListAsync();
        Assert.Equal(retained.Length, remaining.Count);
        Assert.DoesNotContain(remaining, message => message.Id == expired.Id);
        Assert.Single(await context.ProcessedTelegramUpdates.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DeleteExpiredBatchAsync_LargeBacklog_DeletesInBoundedBatches()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        context.TelegramOutboxMessages.AddRange(Enumerable.Range(0, 501)
            .Select(_ => CreateMessage(OutboxMessageStatus.Sent, Now.AddDays(-31))));
        await context.SaveChangesAsync();
        var cleanup = new TelegramOutboxCleanupService(context, new FixedTimeProvider(Now));

        Assert.True((await cleanup.DeleteExpiredBatchAsync(CancellationToken.None)).IsFullBatch);
        Assert.Equal(1, await context.TelegramOutboxMessages.CountAsync());
        Assert.False((await cleanup.DeleteExpiredBatchAsync(CancellationToken.None)).IsFullBatch);
        Assert.Empty(await context.TelegramOutboxMessages.ToListAsync());
        Assert.False((await cleanup.DeleteExpiredBatchAsync(CancellationToken.None)).IsFullBatch);
    }

    [Fact]
    public async Task DeleteExpiredBatchAsync_Cancelled_DoesNotDeleteMessages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        context.TelegramOutboxMessages.Add(CreateMessage(OutboxMessageStatus.Sent, Now.AddDays(-31)));
        await context.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TelegramOutboxCleanupService(context, new FixedTimeProvider(Now))
                .DeleteExpiredBatchAsync(cancellation.Token));

        Assert.Equal(1, await context.TelegramOutboxMessages.CountAsync());
    }

    private static OwnDayDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<OwnDayDbContext>().UseSqlite(connection).Options);

    private static TelegramOutboxMessage CreateMessage(OutboxMessageStatus status, DateTime? sentAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            ChatId = 1,
            Text = "pong",
            Status = status,
            CreatedAt = Now.AddDays(-100),
            NextAttemptAt = Now.AddDays(-100),
            SentAt = sentAt
        };
}
