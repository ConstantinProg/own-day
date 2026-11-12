using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Learning.Requests;
using OwnDay.Application.Learning;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Delivery;
using OwnDay.Infrastructure.Learning;
using Microsoft.Extensions.Logging.Abstractions;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Migrate_LegacyOutbox_PreservesOrderingAndAllocatesLaterOrdersAfterRestart()
    {
        await using (var db = Open())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261112140000_AddLearningProviderSteps");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO telegram_outbox_messages (id, chat_id, text, status, attempt_count, next_attempt_at, created_at)
                VALUES ('00000000-0000-0000-0000-000000000002', 101, 'Second', 0, 0, now(), now()),
                    ('00000000-0000-0000-0000-000000000001', 101, 'First', 0, 0, now(), now());
                """);
            await db.Database.MigrateAsync();
            var old = await db.TelegramOutboxMessages.OrderBy(message => message.DeliveryOrder).ToArrayAsync();
            Assert.Equal(new[] { "First", "Second" }, old.Select(message => message.Text));
        }
        await using var restarted = Open();
        restarted.Add(new TelegramOutboxMessage
        {
            Id = Guid.NewGuid(), ChatId = 101, Text = "Later", CreatedAt = DateTime.UnixEpoch,
            Status = OutboxMessageStatus.Pending, NextAttemptAt = DateTime.UnixEpoch
        });
        await restarted.SaveChangesAsync();
        Assert.Equal(new[] { "First", "Second", "Later" }, await restarted.TelegramOutboxMessages
            .OrderBy(message => message.DeliveryOrder).Select(message => message.Text).ToArrayAsync());
    }

    [Fact]
    public async Task Process_UnknownPhysicalCall_StopsAdmissionUntilActualCompletion()
    {
        await EnqueueAsync("learn", "English");
        var probe = new Probe("generate") { IgnoreCancellation = true };
        using var host = Host(probe, worker: false, configure: options => options.ProviderTimeoutSeconds = 1);
        var gate = host.Services.GetRequiredService<LearningProviderCallGate>();
        async Task<bool> ProcessHeadAsync()
        {
            using var scope = host.Services.CreateScope();
            var lease = (await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
            return await scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default);
        }
        async Task AddOwnerAsync(long owner, int update)
        {
            await using var db = Open();
            db.Add(new LearningRequest
            {
                UserId = new(owner), Sequence = 1, SourceKey = "telegram:update:" + update, SourceUpdateId = update, ChatId = owner,
                Status = LearningRequestStatus.Ready, Outcome = null, ReceivedAt = DateTime.UtcNow, AvailableAt = DateTime.UtcNow,
                NormalizedInput = System.Text.Json.JsonSerializer.Serialize(new LearningCommandInput("learn", "English"), System.Text.Json.JsonSerializerOptions.Web)
            });
            await db.SaveChangesAsync();
        }
        try
        {
            Assert.True(await ProcessHeadAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await AddOwnerAsync(202, 2);
            Assert.True(await ProcessHeadAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, probe.Calls);
        }
        finally { probe.Release.TrySetResult(); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!gate.TryEnter()) await Task.Delay(10, deadline.Token);
        gate.Release();
        await AddOwnerAsync(303, 3);
        Assert.True(await ProcessHeadAsync());
        Assert.Equal(2, probe.Calls);
        await using var observer = Open();
        Assert.Equal(303, (await observer.Set<Lesson>().SingleAsync()).UserId.Value);
        Assert.Equal(2, await observer.Set<LearningProviderAttempt>().CountAsync());
        Assert.All(await observer.Set<LearningProviderAttempt>().ToListAsync(), attempt => Assert.NotNull(attempt.FinishedAt));
    }

    [Fact]
    public async Task Deliver_MachineClockMovesBackwards_PreservesPersistedMessageOrder()
    {
        foreach (var (text, offset) in new[] { ("First", 60), ("Second", -60) })
        {
            await using var db = Open();
            db.Add(new TelegramOutboxMessage
            {
                Id = Guid.NewGuid(), ChatId = 101, Text = text, Status = OutboxMessageStatus.Pending,
                CreatedAt = _now.AddMinutes(offset).UtcDateTime, NextAttemptAt = _now.AddHours(-2).UtcDateTime
            });
            await db.SaveChangesAsync();
        }
        await using var observer = Open();
        var sender = new OrderedSender();
        await new TelegramOutboxDeliveryService(observer, sender, NullLogger<TelegramOutboxDeliveryService>.Instance,
            new Clock(), new EfLessonStore(observer)).DeliverBatchAsync(default);
        Assert.Equal(new[] { "First", "Second" }, sender.Messages);
    }

    private sealed class OrderedSender : ITelegramMessageSender
    {
        public List<string> Messages { get; } = [];
        public Task SendMessageAsync(long chatId, string text, string? replyMarkupJson, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Messages.Add(text); return Task.CompletedTask; }
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("result")]
    [InlineData("apply")]
    public async Task Process_CrashAtCommitBoundary_RecoversWithoutRepeatingCommittedResultsOrEffects(string boundary)
    {
        await EnqueueAsync("learn", "English");
        var probe = new Probe("generate");
        probe.Release.TrySetResult();
        using (var host = Host(probe, worker: false, interceptor: new RejectBoundary(boundary)))
        using (var scope = host.Services.CreateScope())
        {
            var lease = (await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        }
        await using (var db = Open())
        {
            Assert.Empty(await db.Set<Lesson>().ToListAsync());
            Assert.Empty(await db.Set<LearningAppliedOperation>().ToListAsync());
            Assert.Empty(await db.TelegramOutboxMessages.ToListAsync());
            Assert.Equal(boundary == "attempt" ? 0 : 1, probe.Calls);
            await db.Database.ExecuteSqlRawAsync("UPDATE learning_requests SET lease_until = clock_timestamp() - interval '1 second'");
        }
        using (var host = Host(probe, worker: false))
        using (var scope = host.Services.CreateScope())
        {
            var lease = (await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
            Assert.True(await scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        }
        await using var observer = Open();
        Assert.Single(await observer.Set<Lesson>().ToListAsync());
        Assert.Single(await observer.Set<LearningAppliedOperation>().ToListAsync());
        var replies = await observer.TelegramOutboxMessages.OrderBy(message => message.ReplyOrdinal).ToArrayAsync();
        Assert.Equal(new int?[] { 0, 1 }, replies.Select(message => message.ReplyOrdinal));
        Assert.Single(replies.Select(message => message.LearningRequestId).Distinct());
        Assert.Equal(boundary == "result" ? 2 : 1, probe.Calls);
        var attempts = await observer.Set<LearningProviderAttempt>().ToArrayAsync();
        Assert.Equal(probe.Calls, attempts.Length);
        Assert.All(attempts, attempt => Assert.NotNull(attempt.FinishedAt));
    }

    [Theory]
    [InlineData(LessonProviderFailure.HttpError, 2)]
    [InlineData(LessonProviderFailure.InvalidResponse, 1)]
    [InlineData(LessonProviderFailure.PermanentHttpError, 1)]
    public async Task Process_ProviderFailure_RetriesOnlyKnownTransientFailuresWithDistinctBoundedAttempts(LessonProviderFailure reason, int expectedCalls)
    {
        await EnqueueAsync("learn", "English");
        var probe = new Probe("generate") { FailureKind = "generate", FailureReason = reason, FailureCount = 1 };
        probe.Release.TrySetResult();
        using var host = Host(probe, worker: false);
        using var scope = host.Services.CreateScope();
        var lease = (await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
        Assert.True(await scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        Assert.Equal(expectedCalls, probe.Calls);
        await using var observer = Open();
        var attempts = await observer.Set<LearningProviderAttempt>().OrderBy(attempt => attempt.Number).ToListAsync();
        Assert.Equal(expectedCalls, attempts.Count);
        Assert.Equal(expectedCalls, attempts.Select(attempt => attempt.Id).Distinct().Count());
        Assert.Equal(reason == LessonProviderFailure.HttpError ? 1 : 0, await observer.Set<Lesson>().CountAsync());
    }

    [Fact]
    public async Task Process_ReclaimDuringProvider_DoesNotStartReplacementWhileOutcomeIsUnknown()
    {
        await EnqueueAsync("learn", "English");
        var probe = new Probe("generate");
        using var host = Host(probe, worker: false);
        using var first = host.Services.CreateScope();
        var oldLease = (await first.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
        var oldWork = first.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(oldLease, 3, default);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await using (var db = Open())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE learning_requests SET lease_until = clock_timestamp() - interval '1 second' WHERE id = {oldLease.RequestId}");
        using var next = host.Services.CreateScope();
        var lease = (await next.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
        var work = next.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default);
        try
        {
            Assert.True(await work.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(1, probe.Calls);
        }
        finally
        {
            probe.Release.TrySetResult();
            await Task.WhenAll(oldWork, work).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.False(await oldWork);
        await using var observer = Open();
        Assert.Empty(await observer.Set<Lesson>().ToListAsync());
        Assert.Equal("provider_outcome_unknown", (await observer.Set<LearningRequest>().SingleAsync()).Outcome);
        Assert.Single(await observer.Set<LearningAppliedOperation>().ToListAsync());
        Assert.Single(await observer.Set<LearningProviderAttempt>().ToListAsync());
    }

    [Fact]
    public async Task Process_AdapterIgnoresCancellation_EndsLogicalWaitWithoutApplyingLateResult()
    {
        await EnqueueAsync("learn", "English");
        var probe = new Probe("generate") { IgnoreCancellation = true };
        using var host = Host(probe, worker: false, configure: options => options.ProviderTimeoutSeconds = 1);
        using var scope = host.Services.CreateScope();
        var lease = (await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
        var work = scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default);
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(await work.WaitAsync(TimeSpan.FromSeconds(3)));
            await using var observer = Open();
            Assert.Empty(await observer.Set<Lesson>().ToListAsync());
            Assert.Equal("provider_outcome_unknown", (await observer.Set<LearningRequest>().SingleAsync()).Outcome);
        }
        finally
        {
            probe.Release.TrySetResult();
            await work.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await using var result = Open();
        Assert.Empty(await result.Set<Lesson>().ToListAsync());
    }

    private sealed class RejectBoundary(string boundary) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            var reject = boundary switch
            {
                "attempt" => db.ChangeTracker.Entries<LearningProviderAttempt>().Any(),
                "result" => db.ChangeTracker.Entries<LearningProviderStep>().Any(entry => entry.Entity.Result is not null),
                "apply" => db.ChangeTracker.Entries<LearningAppliedOperation>().Any(),
                _ => false
            };
            if (reject) throw new InvalidOperationException("Injected commit boundary failure.");
            return ValueTask.FromResult(result);
        }
    }
}
