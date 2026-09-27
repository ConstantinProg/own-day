using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Actions;
using OwnDay.Application.Inbox;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed class PostgresInboxConcurrencyTests
{
    private static readonly UserId Owner = new(101);

    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public Task ProcessVsProcess_SeparateTransactions_OnlyFirstCreatesTarget() =>
        RunRaceAsync(FirstOperation.Process, SecondOperation.Process);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public Task ProcessVsDiscard_SeparateTransactions_OnlyOneTerminalOutcome(bool processFirst) =>
        RunRaceAsync(processFirst ? FirstOperation.Process : FirstOperation.Discard,
            processFirst ? SecondOperation.Discard : SecondOperation.Process);

    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Process_TargetInsertedThenTransitionFails_RollsBackTargetAndInbox()
    {
        var connectionString = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required for this test.");
        }

        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new OwnDayDbContext(new DbContextOptionsBuilder<OwnDayDbContext>()
            .UseNpgsql(connectionString).Options);
#pragma warning disable EF1003 // Schema name is generated from a GUID.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003
        try
        {
            var options = new DbContextOptionsBuilder<OwnDayDbContext>()
                .UseNpgsql(connectionString + ";Search Path=" + schema).Options;
            var capturedAt = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
            long inboxId;
            await using (var setup = new OwnDayDbContext(options))
            {
                await setup.Database.MigrateAsync();
                inboxId = (await new InboxCaptureService(new EfInboxItemStore(setup), new FixedClock(capturedAt))
                    .CaptureAsync(Owner, "original", CancellationToken.None)).Id;
            }

            await using (var failing = new OwnDayDbContext(options))
            {
                var clock = new FixedClock(capturedAt.AddSeconds(-1));
                var processor = new InboxProcessingService(new EfInboxProcessingStore(failing),
                    new ActionService(new EfActionStore(failing), clock),
                    new StructuredItemService(new EfStructuredItemStore(failing), clock), clock);
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => processor.ProcessAsync(
                    Owner, new(inboxId, InboxTargetKind.Action, "Action"), CancellationToken.None));
            }

            await using var verify = new OwnDayDbContext(options);
            Assert.Empty(await verify.Actions.ToListAsync());
            var inbox = await verify.InboxItems.SingleAsync();
            Assert.Equal(InboxItemStatus.Active, inbox.Status);
            Assert.Null(inbox.TargetId);
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    private static async Task RunRaceAsync(FirstOperation first, SecondOperation second)
    {
        var connectionString = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required for this test.");
        }

        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        var adminOptions = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connectionString).Options;
        await using var admin = new OwnDayDbContext(adminOptions);
#pragma warning disable EF1003 // Schema name is generated from a GUID.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003
        try
        {
            var options = new DbContextOptionsBuilder<OwnDayDbContext>()
                .UseNpgsql(connectionString + ";Search Path=" + schema).Options;
            long inboxId;
            await using (var setup = new OwnDayDbContext(options))
            {
                await setup.Database.MigrateAsync();
                inboxId = (await new InboxCaptureService(new EfInboxItemStore(setup), TimeProvider.System)
                    .CaptureAsync(Owner, "original", CancellationToken.None)).Id;
            }

            await using (var firstDb = new OwnDayDbContext(options))
            await using (var secondDb = new OwnDayDbContext(options))
            {
                await using var firstTransaction = await firstDb.Database.BeginTransactionAsync();
                var firstProcessor = Processor(firstDb);
                var secondProcessor = Processor(secondDb);

                if (first == FirstOperation.Process)
                {
                    var result = await firstProcessor.ProcessAsync(Owner,
                        new(inboxId, InboxTargetKind.Action, "First action"), CancellationToken.None);
                    Assert.Equal(ProcessInboxItemStatus.Processed, result.Status);
                }
                else
                {
                    Assert.Equal(DiscardInboxItemResult.Discarded,
                        await firstProcessor.DiscardAsync(Owner, inboxId, CancellationToken.None));
                }

                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var contender = Task.Run(async () =>
                {
                    started.SetResult();
                    return second == SecondOperation.Process
                        ? (object)await secondProcessor.ProcessAsync(Owner,
                            new(inboxId, InboxTargetKind.Project, "Second project"), CancellationToken.None)
                        : await secondProcessor.DiscardAsync(Owner, inboxId, CancellationToken.None);
                });
                await started.Task;
                await Task.Delay(150);
                Assert.False(contender.IsCompleted);
                await firstTransaction.CommitAsync();

                var outcome = await contender.WaitAsync(TimeSpan.FromSeconds(10));
                if (second == SecondOperation.Process)
                {
                    Assert.Equal(first == FirstOperation.Process
                            ? ProcessInboxItemStatus.AlreadyProcessed
                            : ProcessInboxItemStatus.Discarded,
                        Assert.IsType<ProcessInboxItemResult>(outcome).Status);
                }
                else
                {
                    Assert.Equal(DiscardInboxItemResult.AlreadyProcessed,
                        Assert.IsType<DiscardInboxItemResult>(outcome));
                }
            }

            await using (var verify = new OwnDayDbContext(options))
            {
                var inbox = await verify.InboxItems.SingleAsync();
                Assert.Equal("original", inbox.OriginalText);
                Assert.Equal(0, await verify.Projects.CountAsync());
                if (first == FirstOperation.Process)
                {
                    var action = await verify.Actions.SingleAsync();
                    Assert.Equal(InboxItemStatus.Processed, inbox.Status);
                    Assert.Equal(InboxTargetKind.Action, inbox.TargetKind);
                    Assert.Equal(action.Id, inbox.TargetId);
                    Assert.NotNull(inbox.ProcessedAt);
                    Assert.Null(inbox.DiscardedAt);
                }
                else
                {
                    Assert.Empty(await verify.Actions.ToListAsync());
                    Assert.Equal(InboxItemStatus.Discarded, inbox.Status);
                    Assert.Null(inbox.TargetKind);
                    Assert.Null(inbox.TargetId);
                    Assert.NotNull(inbox.DiscardedAt);
                }
            }
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    private static InboxProcessingService Processor(OwnDayDbContext db) =>
        new(new EfInboxProcessingStore(db), new ActionService(new EfActionStore(db), TimeProvider.System),
            new StructuredItemService(new EfStructuredItemStore(db), TimeProvider.System), TimeProvider.System);

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private enum FirstOperation { Process, Discard }
    private enum SecondOperation { Process, Discard }
}
