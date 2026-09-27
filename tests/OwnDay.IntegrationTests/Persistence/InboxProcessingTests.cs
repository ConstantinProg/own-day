using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Actions;
using OwnDay.Application.Inbox;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed class InboxProcessingTests
{
    private static readonly UserId Alice = new(101);
    private static readonly UserId Bob = new(102);
    private static readonly DateTime Now = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(InboxTargetKind.Action)]
    [InlineData(InboxTargetKind.Project)]
    [InlineData(InboxTargetKind.SomedayMaybe)]
    [InlineData(InboxTargetKind.Reference)]
    [InlineData(InboxTargetKind.WaitingFor)]
    public async Task Process_EachTarget_CreatesOneTargetAndPreservesProvenance(InboxTargetKind kind)
    {
        await using var connection = await OpenAsync();
        var options = Options(connection);
        long inboxId;
        long? projectId;
        await using (var db = new OwnDayDbContext(options))
        {
            var clock = new FixedClock();
            inboxId = (await Capture(db, clock, Alice, "  original\ntext  ")).Id;
            projectId = kind is InboxTargetKind.Action or InboxTargetKind.SomedayMaybe or InboxTargetKind.Reference or InboxTargetKind.WaitingFor
                ? (await Structured(db, clock).CreateProjectAsync(Alice, "Owned project", CancellationToken.None)).Id
                : null;
        }

        await using (var db = new OwnDayDbContext(options))
        {
            var clock = new FixedClock();
            var result = await Processor(db, clock).ProcessAsync(Alice,
                new(inboxId, kind, "Edited result", projectId,
                    kind == InboxTargetKind.WaitingFor ? "Alice" : null,
                    kind == InboxTargetKind.WaitingFor ? Now.AddMinutes(-2) : null), CancellationToken.None);
            Assert.Equal(ProcessInboxItemStatus.Processed, result.Status);
            Assert.Equal(kind, result.TargetKind);
            Assert.True(result.TargetId > 0);
            Assert.Equal(1, await TargetCountAsync(db, kind));
            Assert.Equal(kind == InboxTargetKind.Project ? 0 : kind == InboxTargetKind.Action ? 1 : 0,
                await db.Actions.CountAsync());
            var item = await db.InboxItems.AsNoTracking().SingleAsync();
            Assert.Equal(InboxItemStatus.Processed, item.Status);
            Assert.Equal("  original\ntext  ", item.OriginalText);
            Assert.Equal(Now, item.CapturedAt);
            Assert.Equal(Now, item.ProcessedAt);
            Assert.Equal(kind, item.TargetKind);
            Assert.Equal(result.TargetId, item.TargetId);
            Assert.Null(item.DiscardedAt);
            if (kind == InboxTargetKind.WaitingFor)
            {
                var waiting = await db.WaitingFors.SingleAsync();
                Assert.Equal("Alice", waiting.Source);
                Assert.Equal(Now.AddMinutes(-2), waiting.WaitingSince);
                Assert.Equal(projectId, waiting.ProjectId);
            }

            var retry = await Processor(db, clock).ProcessAsync(Alice,
                new(inboxId, InboxTargetKind.Action, "Another target"), CancellationToken.None);
            Assert.Equal(ProcessInboxItemStatus.AlreadyProcessed, retry.Status);
            Assert.Equal(result.TargetId, retry.TargetId);
            Assert.Equal(1, await TargetCountAsync(db, kind));
            Assert.Equal(DiscardInboxItemResult.AlreadyProcessed,
                await Processor(db, clock).DiscardAsync(Alice, inboxId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Process_UnknownForeignAndTerminalItems_ReturnSafeOutcomes()
    {
        await using var connection = await OpenAsync();
        var options = Options(connection);
        await using var db = new OwnDayDbContext(options);
        var clock = new FixedClock();
        var item = await Capture(db, clock, Alice, "raw");
        var processor = Processor(db, clock);
        Assert.Equal(ProcessInboxItemStatus.NotFound,
            (await processor.ProcessAsync(Bob, new(item.Id, InboxTargetKind.Action, "Foreign"), CancellationToken.None)).Status);
        Assert.Equal(ProcessInboxItemStatus.NotFound,
            (await processor.ProcessAsync(Alice, new(999, InboxTargetKind.Action, "Unknown"), CancellationToken.None)).Status);
        Assert.Equal(DiscardInboxItemResult.NotFound, await processor.DiscardAsync(Bob, item.Id, CancellationToken.None));
        Assert.Equal(DiscardInboxItemResult.Discarded, await processor.DiscardAsync(Alice, item.Id, CancellationToken.None));
        Assert.Equal(DiscardInboxItemResult.AlreadyDiscarded, await processor.DiscardAsync(Alice, item.Id, CancellationToken.None));
        Assert.Equal(ProcessInboxItemStatus.Discarded,
            (await processor.ProcessAsync(Alice, new(item.Id, InboxTargetKind.Action, "Later"), CancellationToken.None)).Status);
        Assert.Empty(await db.Actions.ToListAsync());
        Assert.Empty(await new InboxCaptureService(new EfInboxItemStore(db), clock).GetActiveAsync(Alice, CancellationToken.None));
        var persisted = await db.InboxItems.AsNoTracking().SingleAsync();
        Assert.Equal("raw", persisted.OriginalText);
        Assert.Equal(Now, persisted.DiscardedAt);
        Assert.Null(persisted.TargetId);
    }

    [Fact]
    public async Task Process_InvalidDataOrProject_DoesNotMutateInboxOrCreateTarget()
    {
        await using var connection = await OpenAsync();
        var options = Options(connection);
        await using var db = new OwnDayDbContext(options);
        var clock = new FixedClock();
        var item = await Capture(db, clock, Alice, "raw");
        var foreignProject = await Structured(db, clock).CreateProjectAsync(Bob, "Foreign", CancellationToken.None);
        var processor = Processor(db, clock);
        foreach (var command in new[]
        {
            new ProcessInboxItem(item.Id, InboxTargetKind.Action, " "),
            new ProcessInboxItem(item.Id, (InboxTargetKind)99, "Title"),
            new ProcessInboxItem(item.Id, InboxTargetKind.Project, "Project", foreignProject.Id)
        })
        {
            Assert.Equal(ProcessInboxItemStatus.InvalidTargetData,
                (await processor.ProcessAsync(Alice, command, CancellationToken.None)).Status);
        }

        foreach (var projectId in new[] { foreignProject.Id, 9999L })
        {
            Assert.Equal(ProcessInboxItemStatus.InvalidProject,
                (await processor.ProcessAsync(Alice,
                    new(item.Id, InboxTargetKind.Action, "Action", projectId), CancellationToken.None)).Status);
        }

        Assert.Equal(InboxItemStatus.Active, (await db.InboxItems.AsNoTracking().SingleAsync(row => row.Id == item.Id)).Status);
        Assert.Empty(await db.Actions.ToListAsync());
    }

    [Fact]
    public async Task Process_CancellationOrFailure_RollsBackTargetAndInboxTransition()
    {
        await using var connection = await OpenAsync();
        var options = Options(connection);
        long id;
        await using (var db = new OwnDayDbContext(options))
        {
            id = (await Capture(db, new FixedClock(), Alice, "raw")).Id;
        }

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await using var db = new OwnDayDbContext(options);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Processor(db, new FixedClock()).ProcessAsync(
                Alice, new(id, InboxTargetKind.Action, "Action"), cancelled.Token));
        }

        // A timestamp before capture fails after the target INSERT, forcing transaction rollback.
        await using (var db = new OwnDayDbContext(options))
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Processor(db,
                new FixedClock(Now.AddSeconds(-1))).ProcessAsync(
                Alice, new(id, InboxTargetKind.Action, "Action"), CancellationToken.None));
        }

        await using (var db = new OwnDayDbContext(options))
        {
            Assert.Empty(await db.Actions.ToListAsync());
            var item = await db.InboxItems.SingleAsync();
            Assert.Equal(InboxItemStatus.Active, item.Status);
            Assert.Null(item.TargetId);
            Assert.Equal(ProcessInboxItemStatus.Processed,
                (await Processor(db, new FixedClock()).ProcessAsync(Alice,
                    new(id, InboxTargetKind.Action, "Action"), CancellationToken.None)).Status);
        }
    }

    [Fact]
    public async Task Process_ExistingOuterTransaction_JoinsAndRollsBackWithCaller()
    {
        await using var connection = await OpenAsync();
        var options = Options(connection);
        long id;
        await using (var setup = new OwnDayDbContext(options))
        {
            id = (await Capture(setup, new FixedClock(), Alice, "raw")).Id;
        }

        await using (var db = new OwnDayDbContext(options))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            Assert.Equal(ProcessInboxItemStatus.Processed,
                (await Processor(db, new FixedClock()).ProcessAsync(Alice,
                    new(id, InboxTargetKind.Project, "Project"), CancellationToken.None)).Status);
            await transaction.RollbackAsync();
        }

        await using (var verify = new OwnDayDbContext(options))
        {
            Assert.Empty(await verify.Projects.ToListAsync());
            var item = await verify.InboxItems.SingleAsync();
            Assert.Equal(InboxItemStatus.Active, item.Status);
            Assert.Null(item.TargetId);
        }
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new OwnDayDbContext(Options(connection));
        await db.Database.EnsureCreatedAsync();
        return connection;
    }

    private static DbContextOptions<OwnDayDbContext> Options(SqliteConnection connection) =>
        new DbContextOptionsBuilder<OwnDayDbContext>().UseSqlite(connection).Options;

    private static Task<InboxItem> Capture(OwnDayDbContext db, TimeProvider clock, UserId userId, string text) =>
        new InboxCaptureService(new EfInboxItemStore(db), clock).CaptureAsync(userId, text, CancellationToken.None);

    private static StructuredItemService Structured(OwnDayDbContext db, TimeProvider clock) =>
        new(new EfStructuredItemStore(db), clock);

    private static InboxProcessingService Processor(OwnDayDbContext db, TimeProvider clock) =>
        new(new EfInboxProcessingStore(db), new ActionService(new EfActionStore(db), clock),
            Structured(db, clock), clock);

    private static Task<int> TargetCountAsync(OwnDayDbContext db, InboxTargetKind kind) => kind switch
    {
        InboxTargetKind.Action => db.Actions.CountAsync(),
        InboxTargetKind.Project => db.Projects.CountAsync(),
        InboxTargetKind.SomedayMaybe => db.SomedayMaybes.CountAsync(),
        InboxTargetKind.Reference => db.References.CountAsync(),
        InboxTargetKind.WaitingFor => db.WaitingFors.CountAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private sealed class FixedClock(DateTime? now = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now ?? Now);
    }
}
