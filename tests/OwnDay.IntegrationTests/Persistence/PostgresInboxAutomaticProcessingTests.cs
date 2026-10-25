using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.OpenAIAnalysis;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

[Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
public sealed class PostgresInboxAutomaticProcessingTests : IAsyncLifetime
{
    private readonly string _schema = "ownday_test_" + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
    private static readonly UserId Owner = new(41);
    private OwnDayDbContext _admin = null!;
    private DbContextOptions<OwnDayDbContext> _options = null!;
    private string _connection = null!;
    private CancellationToken Token => _deadline.Token;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required for this test.");
        _admin = new(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connection).Options);
#pragma warning disable EF1003 // Generated GUID schema, never user input.
        await _admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + _schema, Token);
#pragma warning restore EF1003
        _connection = new NpgsqlConnectionStringBuilder(connection) { SearchPath = _schema }.ConnectionString;
        _options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(_connection).Options;
        await using var db = Db();
        await db.Database.MigrateAsync(Token);
    }

    public async Task DisposeAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
#pragma warning disable EF1003 // Same generated schema.
        await _admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + _schema + " CASCADE", cleanup.Token);
#pragma warning restore EF1003
        await _admin.DisposeAsync();
        using var pool = new NpgsqlConnection(_connection);
        NpgsqlConnection.ClearPool(pool);
        _deadline.Dispose();
    }

    [Theory]
    [InlineData(InboxTargetKind.Action)]
    [InlineData(InboxTargetKind.Project)]
    [InlineData(InboxTargetKind.WaitingFor)]
    [InlineData(InboxTargetKind.Reference)]
    [InlineData(InboxTargetKind.SomedayMaybe)]
    public async Task ApplyAsync_EachKind_PersistsTargetOriginalAndAuditWithoutReapplying(InboxTargetKind kind)
    {
        var proposal = Proposal(kind) with { Description = "Extra source detail", Ambiguities = ["Uncertain"] };
        var (itemId, analysisId) = await SeedAsync([proposal]);
        await using (var db = Db())
        {
            var result = await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
            Assert.NotNull(result);
            Assert.Equal(InboxPolicyRoute.AutoApply, result.Route);
            Assert.Equal("inbox-policy-v2", result.PolicyVersion);
            Assert.Contains("Extra source detail", result.ProposalsJson);
        }
        // A fresh process/scope sees the committed application and returns exactly the same identities.
        await using var restarted = Db();
        var repeated = await Service(restarted).ApplyAsync(Owner, itemId, analysisId, Token);
        var item = await restarted.InboxItems.SingleAsync(Token);
        Assert.Equal(InboxItemStatus.Processed, item.Status);
        Assert.Equal("  исходная запись завтра  ", item.OriginalText);
        var target = Assert.Single(item.Targets);
        Assert.Equal(kind, target.Kind);
        var audit = Assert.Single(await restarted.InboxAutomaticApplications.ToListAsync(Token));
        var applied = Assert.Single(JsonSerializer.Deserialize<AppliedInboxProposal[]>(audit.TargetsJson, InboxAnalysisJson.Options)!);
        Assert.Equal(target.TargetId, applied.TargetId);
        Assert.Equal(Assert.Single((await restarted.InboxAnalyses.Include(a => a.Proposals).SingleAsync(Token)).Proposals).Id, applied.ProposalId);
        Assert.Equal(audit.TargetsJson, repeated!.TargetsJson);
        Assert.True(audit.EvaluatedAt >= item.CapturedAt);
        Assert.Equal(1, await CountTargetsAsync(restarted));
        var notification = Assert.Single(await restarted.TelegramOutboxMessages.ToListAsync(Token));
        Assert.Equal(analysisId, notification.Id);
        Assert.Null(notification.ReplyMarkupJson);
        Assert.Contains("/inbox_result " + itemId, notification.Text, StringComparison.Ordinal);
        if (kind == InboxTargetKind.WaitingFor)
        {
            var waiting = await restarted.WaitingFors.SingleAsync(Token);
            Assert.True(waiting.WaitingSince >= item.CapturedAt);
            Assert.True(waiting.WaitingSince <= item.ProcessedAt);
        }
    }

    [Fact]
    public async Task ApplyAsync_Composite_CreatesTwoActionsAndSomedayMaybeAtomically()
    {
        var (itemId, analysisId) = await SeedAsync(Composite());
        await using var db = Db();
        await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        Assert.Equal(2, await db.Actions.CountAsync(Token));
        Assert.Equal(1, await db.SomedayMaybes.CountAsync(Token));
        var item = await db.InboxItems.SingleAsync(Token);
        Assert.Equal(3, item.Targets.Count);
        var notification = Assert.Single(await db.TelegramOutboxMessages.ToListAsync(Token));
        Assert.Equal(3, notification.Text.Split('\n').Count(line => line.StartsWith("Task #", StringComparison.Ordinal) ||
            line.StartsWith("Idea #", StringComparison.Ordinal)));
        Assert.Equal(3, JsonSerializer.Deserialize<AppliedInboxProposal[]>(
            (await db.InboxAutomaticApplications.SingleAsync(Token)).TargetsJson, InboxAnalysisJson.Options)!.Length);
    }

    [Fact]
    public async Task ApplyAsync_WaitingWithExplicitStartAndSource_PreservesBoth()
    {
        var since = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        var (itemId, analysisId) = await SeedAsync([Proposal(InboxTargetKind.WaitingFor) with
        {
            Properties = Empty with
            {
                Source = "Ивана", WaitingSince = since, WaitingSinceEvidence = "2026-10-20T09:00:00Z"
            }
        }], original: "Жду договор от Ивана с 2026-10-20T09:00:00Z");
        await using var db = Db();
        await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        var waiting = await db.WaitingFors.SingleAsync(Token);
        Assert.Equal(since.UtcDateTime, waiting.WaitingSince);
        Assert.Equal("Ивана", waiting.Source);
    }

    [Fact]
    public async Task EvaluateAsync_DefaultPolicy_UsesOnlyV2ForCurrentAssessment()
    {
        var (itemId, analysisId) = await SeedAsync([Proposal()]);
        await using var db = Db();
        var service = new InboxPolicyService(new EfInboxPolicyStore(db), new());
        var result = await service.EvaluateAsync(Owner, itemId, analysisId, Token);
        Assert.Equal(InboxPolicyEngine.Version, result.PolicyVersion);
        Assert.Equal(InboxPolicyRoute.AutoApply, result.Route);
        Assert.Equal(InboxPolicyRoute.RequireConfirmation,
            (await service.EvaluateAsync(Owner, itemId, analysisId, InboxPolicyEngine.LegacyVersion, Token)).Route);
        Assert.Equal(InboxItemStatus.Active, (await db.InboxItems.SingleAsync(Token)).Status);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("current")]
    [InlineData("ambiguous")]
    [InlineData("completed")]
    [InlineData("renamed")]
    [InlineData("deleted")]
    [InlineData("foreign")]
    [InlineData("truncated")]
    public async Task ApplyAsync_OptionalProject_OnlyLinksOneCurrentUnchangedOwnedProject(string scenario)
    {
        await using var seed = Db();
        var project = Project.Create(Owner, "Project", DateTime.UtcNow);
        var other = Project.Create(Owner, "Other", DateTime.UtcNow);
        seed.Projects.AddRange(project, other);
        await seed.SaveChangesAsync(Token);
        await seed.Entry(project).ReloadAsync(Token);
        await seed.Entry(other).ReloadAsync(Token);
        var context = new InboxAnalysisContext(null,
            [new(project.Id, project.Title, project.CreatedAt), new(other.Id, other.Title, other.CreatedAt)], scenario == "truncated");
        var proposal = Proposal() with
        {
            ProjectIds = scenario is "absent" or "truncated" ? [] : scenario == "ambiguous" ? [project.Id, other.Id] : [project.Id],
            Ambiguities = ["Possible relation"], Properties = Empty with { UnresolvedDateText = "завтра" }
        };
        var (itemId, analysisId) = await SeedAsync([proposal], context);
        switch (scenario)
        {
            case "completed": project.Complete(DateTime.UtcNow); break;
            case "renamed": seed.Entry(project).Property(p => p.Title).CurrentValue = "New meaning"; break;
            case "deleted": seed.Projects.Remove(project); break;
            case "foreign":
                await seed.Projects.Where(p => p.Id == project.Id).ExecuteUpdateAsync(
                    s => s.SetProperty(p => p.UserId, new UserId(42)), Token);
                break;
        }
        await seed.SaveChangesAsync(Token);
        await using var db = Db();
        var result = await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        Assert.Equal(InboxPolicyRoute.AutoApply, result!.Route);
        Assert.Equal(scenario == "current" ? project.Id : (long?)null, (await db.Actions.SingleAsync(Token)).ProjectId);
        Assert.Equal(proposal.ProjectIds, (await db.InboxAnalyses.Include(a => a.Proposals).SingleAsync(Token)).Proposals[0].ReadProposal().ProjectIds);
    }

    [Theory]
    [InlineData("superseded")]
    [InlineData("input-changed")]
    [InlineData("discarded")]
    [InlineData("processed")]
    [InlineData("invalid-proposal")]
    public async Task ApplyAsync_StaleInactiveOrInvalidBatch_KeepsDataAndCreatesNoAutomaticTargets(string scenario)
    {
        var proposals = Composite();
        if (scenario == "invalid-proposal")
        {
            proposals[2] = proposals[2] with { Title = " " };
        }
        var (itemId, analysisId) = await SeedAsync(proposals);
        await using (var writer = Db())
        {
            var item = await writer.InboxItems.SingleAsync(Token);
            switch (scenario)
            {
                case "superseded":
                    var old = await writer.InboxAnalyses.SingleAsync(Token);
                    writer.InboxAnalyses.Add(InboxAnalysis.Start(Owner, itemId, 2, new("fake", "inbox-v1", "test"),
                        old.ContextJson, old.ContextFingerprint, old.InputFingerprint, DateTime.UtcNow));
                    break;
                case "input-changed": writer.Entry(item).Property(i => i.OriginalText).CurrentValue = "Updated text"; break;
                case "discarded": item.Discard(DateTime.UtcNow); break;
                case "processed":
                    await Processing(writer).ProcessAsync(Owner, new(itemId, InboxTargetKind.Reference, "Manual"), Token);
                    break;
            }
            await writer.SaveChangesAsync(Token);
        }
        await using var db = Db();
        var result = await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        Assert.Equal(InboxPolicyRoute.KeepInInbox, result!.Route);
        Assert.Equal("[]", result.TargetsJson);
        Assert.Empty(await db.Actions.ToListAsync(Token));
        Assert.Empty(await db.SomedayMaybes.ToListAsync(Token));
        Assert.Equal(scenario == "processed" ? 1 : 0, await CountTargetsAsync(db));
        Assert.Single(await db.InboxAutomaticApplications.ToListAsync(Token));
    }

    [Fact]
    public async Task ProcessNextAsync_ConcurrentWorkers_CommitOneBatchAndOneAudit()
    {
        await SeedAsync(Composite());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task RunAsync()
        {
            await ready.Task.WaitAsync(Token);
            await using var db = Db();
            await Service(db).ProcessNextAsync(Token);
        }
        var tasks = Enumerable.Range(0, 4).Select(_ => RunAsync()).ToArray();
        ready.SetResult();
        await Task.WhenAll(tasks).WaitAsync(Token);
        await using var verify = Db();
        Assert.Equal(3, await CountTargetsAsync(verify));
        Assert.Single(await verify.InboxAutomaticApplications.ToListAsync(Token));
        Assert.Single(await verify.TelegramOutboxMessages.ToListAsync(Token));
        Assert.False(await Service(verify).ProcessNextAsync(Token));
    }

    [Theory]
    [InlineData("someday_maybes")]
    [InlineData("inbox_automatic_applications")]
    [InlineData("telegram_outbox_messages")]
    public async Task ApplyAsync_FailureAfterEarlierInserts_RollsBackWholeBatchAndRecovers(string failTable)
    {
        var (itemId, analysisId) = await SeedAsync(Composite());
        var fault = new FailInsert(failTable);
        await using (var failing = Db(fault))
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => Service(failing).ApplyAsync(Owner, itemId, analysisId, Token));
            Assert.True(fault.Fired);
        }
        await using (var verify = Db())
        {
            Assert.Equal(0, await CountTargetsAsync(verify));
            Assert.Empty(await verify.InboxAutomaticApplications.ToListAsync(Token));
            Assert.Empty(await verify.TelegramOutboxMessages.ToListAsync(Token));
            var item = await verify.InboxItems.SingleAsync(Token);
            Assert.Equal(InboxItemStatus.Active, item.Status);
            Assert.Empty(item.Targets);
            Assert.Equal(3, (await verify.InboxAnalyses.Include(a => a.Proposals).SingleAsync(Token)).Proposals.Count);
        }
        // Durable completed analysis is sufficient to discover and resume work, with no provider call.
        await using var restarted = Db();
        Assert.True(await Service(restarted).ProcessNextAsync(Token));
        Assert.False(await Service(restarted).ProcessNextAsync(Token));
        Assert.Equal(3, await CountTargetsAsync(restarted));
        Assert.Single(await restarted.TelegramOutboxMessages.ToListAsync(Token));
    }

    [Fact]
    public async Task ApplyAsync_CommitAcknowledgementLost_RestartReturnsCommittedTargets()
    {
        var (itemId, analysisId) = await SeedAsync(Composite());
        await using (var failing = Db(new LostCommitAcknowledgement()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(failing).ApplyAsync(Owner, itemId, analysisId, Token));
        }
        await using var restarted = Db();
        var result = await Service(restarted).ApplyAsync(Owner, itemId, analysisId, Token);
        Assert.Equal(InboxPolicyRoute.AutoApply, result!.Route);
        Assert.Equal(3, await CountTargetsAsync(restarted));
        Assert.Single(await restarted.InboxAutomaticApplications.ToListAsync(Token));
        Assert.Single(await restarted.TelegramOutboxMessages.ToListAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_CapturedOrAlreadyAnalyzedInbox_AutomaticallyAppliesWithoutAnotherProviderCall(bool alreadyAnalyzed)
    {
        var ai = new FakeAnalyzer();
        if (alreadyAnalyzed)
        {
            await SeedAsync(Composite());
        }
        else
        {
            await using var capture = Db();
            await new EfInboxItemStore(capture).AddAsync(InboxItem.Capture(Owner, "Купить молоко, позвонить Ивану и когда-нибудь попробовать изучить японский", DateTime.UtcNow), Token);
        }
        // Restart with a completed analysis also works with provider/background analysis switched off.
        using var host = new WorkerHost(_connection, ai, !alreadyAnalyzed);
        using var client = host.CreateClient();
        while (true)
        {
            await using var db = Db();
            if (await db.InboxAutomaticApplications.AnyAsync(Token))
            {
                Assert.Equal(InboxItemStatus.Processed, (await db.InboxItems.SingleAsync(Token)).Status);
                Assert.Equal(3, await CountTargetsAsync(db));
                if (alreadyAnalyzed)
                {
                    Assert.Single(await db.TelegramOutboxMessages.ToListAsync(Token));
                }
                break;
            }
            await Task.Delay(50, Token);
        }
        Assert.Equal(alreadyAnalyzed ? 0 : 1, ai.Calls);
    }

    [Fact]
    public async Task ApplyAsync_ForeignOwnerAndCancellation_DoNotMutate()
    {
        var (itemId, analysisId) = await SeedAsync(Composite());
        await using var db = Db();
        Assert.Null(await Service(db).ApplyAsync(new(42), itemId, analysisId, Token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(db).ApplyAsync(Owner, itemId, analysisId, cancelled.Token));
        Assert.Equal(0, await CountTargetsAsync(db));
        Assert.Empty(await db.InboxAutomaticApplications.ToListAsync(Token));
    }

    [Fact]
    public async Task ApplyAsync_ManualProcessingWinsLock_DoesNotReplaceManualTarget()
    {
        var (itemId, analysisId) = await SeedAsync(Composite());
        await using var manual = Db();
        await using var transaction = await manual.Database.BeginTransactionAsync(Token);
        var result = await Processing(manual).ProcessAsync(Owner, new(itemId, InboxTargetKind.Reference, "Manual"), Token);
        var lockAttempt = new ObserveInboxLock();
        await using var automatic = Db(lockAttempt);
        var applying = Service(automatic).ApplyAsync(Owner, itemId, analysisId, Token);
        await lockAttempt.Attempted.Task.WaitAsync(Token);
        Assert.False(applying.IsCompleted);
        await transaction.CommitAsync(Token);
        Assert.Equal(InboxPolicyRoute.KeepInInbox, (await applying)!.Route);
        await using var verify = Db();
        var item = await verify.InboxItems.SingleAsync(Token);
        Assert.Equal(result.TargetId, Assert.Single(item.Targets).TargetId);
        Assert.Equal(InboxTargetKind.Reference, item.TargetKind);
        Assert.Equal(1, await CountTargetsAsync(verify));
        Assert.Empty(await verify.TelegramOutboxMessages.ToListAsync(Token));
    }

    [Fact]
    public async Task ApplyAsync_ProjectChangesDuringApplication_HoldsSharedLockUntilCommit()
    {
        await using var seed = Db();
        var project = Project.Create(Owner, "Project", DateTime.UtcNow);
        seed.Projects.Add(project);
        await seed.SaveChangesAsync(Token);
        await seed.Entry(project).ReloadAsync(Token);
        var (itemId, analysisId) = await SeedAsync([Proposal() with { ProjectIds = [project.Id] }],
            new(null, [new(project.Id, project.Title, project.CreatedAt)], false));
        var gate = new PauseAfterProjectRead();
        await using var automatic = Db(gate);
        var applying = Service(automatic).ApplyAsync(Owner, itemId, analysisId, Token);
        try
        {
            await gate.Read.Task.WaitAsync(Token);
            await using var writer = Db();
            await using var transaction = await writer.Database.BeginTransactionAsync(Token);
            await writer.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '200ms'", Token);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => writer.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE projects SET title = 'Changed' WHERE id = {project.Id}", Token));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, exception.SqlState);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        await applying;
        await using var verify = Db();
        Assert.Equal(project.Id, (await verify.Actions.SingleAsync(Token)).ProjectId);
        Assert.Equal(ProjectStatus.Active, (await verify.Projects.SingleAsync(Token)).Status);
    }

    [Fact]
    public async Task ApplyAsync_CommitRejected_RollsBackDomainAndAuditAndRetriesSavedAnalysis()
    {
        var (itemId, analysisId) = await SeedAsync(Composite());
        await using (var db = Db())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_application() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'test failure'; END $$;
                CREATE CONSTRAINT TRIGGER reject_application AFTER INSERT ON inbox_automatic_applications
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION reject_application();
                """, Token);
            await Assert.ThrowsAsync<PostgresException>(() => Service(db).ApplyAsync(Owner, itemId, analysisId, Token));
        }
        await using var restarted = Db();
        Assert.Equal(0, await CountTargetsAsync(restarted));
        Assert.Empty(await restarted.InboxAutomaticApplications.ToListAsync(Token));
        Assert.Equal(InboxItemStatus.Active, (await restarted.InboxItems.SingleAsync(Token)).Status);
        await restarted.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_application ON inbox_automatic_applications", Token);
        Assert.True(await Service(restarted).ProcessNextAsync(Token));
        Assert.Equal(3, await CountTargetsAsync(restarted));
    }

    [Fact]
    public async Task Migrate_PreviousManualEntry_BackfillsTargetsWithoutChangingLegacyFields()
    {
        await using var db = Db();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261025121850_AddInboxAnalysisParameters", Token);
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inbox_items (user_id, original_text, captured_at, status, processed_at, target_kind, target_id)
            VALUES ({Owner.Value}, 'legacy', {now}, 1, {now}, {(int)InboxTargetKind.Action}, 17)
            """, Token);
        await migrator.MigrateAsync(cancellationToken: Token);
        var item = await db.InboxItems.SingleAsync(Token);
        var target = Assert.Single(item.Targets);
        Assert.Equal(17, target.TargetId);
        Assert.Equal(InboxTargetKind.Action, target.Kind);
        Assert.Equal(item.TargetId, target.TargetId);
        Assert.Equal("legacy", item.OriginalText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmAsync_ConcurrentAcceptAndReject_CommitExactlyOneResolution(bool acceptFirst)
    {
        var (itemId, analysisId) = await SeedAsync([Proposal()]);
        await using (var db = Db())
        {
            var result = await Service(db).ApplyAsync(Owner, itemId, analysisId, InboxPolicyEngine.LegacyVersion, Token);
            Assert.Equal(InboxPolicyRoute.RequireConfirmation, result!.Route);
        }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<InboxConfirmationResult> RunAsync(bool accept)
        {
            await ready.Task.WaitAsync(Token);
            await using var db = Db();
            return await Service(db).ConfirmAsync(Owner, analysisId, accept, Token);
        }
        var tasks = new[] { RunAsync(acceptFirst), RunAsync(!acceptFirst), RunAsync(true), RunAsync(false) };
        ready.SetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(Token);
        var winner = Assert.Single(results, r => r.Changed);
        Assert.All(results, r => Assert.Equal(winner.Outcome, r.Outcome));
        await using var verify = Db();
        Assert.Equal(winner.Outcome == InboxConfirmationOutcome.Applied ? 1 : 0, await CountTargetsAsync(verify));
        Assert.Equal(2, await verify.TelegramOutboxMessages.CountAsync(Token)); // Preview and one resolution.
        Assert.NotNull((await verify.InboxAutomaticApplications.SingleAsync(Token)).ResolvedAt);
    }

    [Fact]
    public async Task ConfirmAsync_OutboxFailure_RollsBackResolutionAndTargetsAndRecoversAfterRestart()
    {
        var (itemId, analysisId) = await SeedAsync([Proposal()]);
        await using (var db = Db())
        {
            await Service(db).ApplyAsync(Owner, itemId, analysisId, InboxPolicyEngine.LegacyVersion, Token);
        }
        await using (var failing = Db(new FailInsert("telegram_outbox_messages")))
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => Service(failing).ConfirmAsync(Owner, analysisId, true, Token));
        }
        await using var restarted = Db();
        Assert.Null((await restarted.InboxAutomaticApplications.SingleAsync(Token)).ConfirmationOutcome);
        Assert.Empty(await restarted.Actions.ToListAsync(Token));
        Assert.Single(await restarted.TelegramOutboxMessages.ToListAsync(Token));
        Assert.Equal(InboxConfirmationOutcome.Applied, (await Service(restarted).ConfirmAsync(Owner, analysisId, true, Token)).Outcome);
        Assert.Single(await restarted.Actions.ToListAsync(Token));
        Assert.Equal(2, await restarted.TelegramOutboxMessages.CountAsync(Token));
    }

    [Fact]
    public async Task ApplyAsync_NoTelegramOrigin_DoesNotInventChatDestination()
    {
        var (itemId, analysisId) = await SeedAsync([Proposal()]);
        await using var db = Db();
        await db.TelegramInboxDestinations.ExecuteDeleteAsync(Token);
        await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        Assert.Single(await db.Actions.ToListAsync(Token));
        Assert.Empty(await db.TelegramOutboxMessages.ToListAsync(Token));
    }

    [Fact]
    public async Task ConfirmAsync_Cancelled_DoesNotResolveOfferOrCreateTargets()
    {
        var (itemId, analysisId) = await SeedAsync([Proposal()]);
        await using var db = Db();
        await Service(db).ApplyAsync(Owner, itemId, analysisId, InboxPolicyEngine.LegacyVersion, Token);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(db).ConfirmAsync(Owner, analysisId, true, cancelled.Token));
        Assert.Null((await db.InboxAutomaticApplications.SingleAsync(Token)).ConfirmationOutcome);
        Assert.Single(await db.TelegramOutboxMessages.ToListAsync(Token));
        Assert.Empty(await db.Actions.ToListAsync(Token));
    }

    private OwnDayDbContext Db(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<OwnDayDbContext>(_options).AddInterceptors(interceptors).Options);

    private static InboxProcessingService Processing(OwnDayDbContext db) =>
        new(new EfInboxProcessingStore(db), new EfActionStore(db), new EfStructuredItemStore(db), TimeProvider.System);

    private static InboxAutomaticProcessingService Service(OwnDayDbContext db) =>
        new(new EfInboxProcessingStore(db), new EfInboxAutomaticProcessingStore(db), new(), Processing(db), TimeProvider.System);

    private async Task<(long ItemId, Guid AnalysisId)> SeedAsync(InboxProposal[] proposals, InboxAnalysisContext? context = null,
        string? original = null)
    {
        await using var db = Db();
        var now = DateTime.UtcNow;
        var item = InboxItem.Capture(Owner, original ?? "  исходная запись завтра  ", now);
        db.InboxItems.Add(item);
        await db.SaveChangesAsync(Token);
        var json = JsonSerializer.Serialize(context ?? new(null, [], false), InboxAnalysisJson.Options);
        var analysis = InboxAnalysis.Start(Owner, item.Id, 1, new("fake", "inbox-v1", "test"), json,
            Hash(json), Hash(item.OriginalText), now);
        analysis.Finish(InboxAnalysisStatus.Completed, now, "{}", new() { Items = proposals });
        db.InboxAnalyses.Add(analysis);
        db.TelegramInboxDestinations.Add(new() { InboxItemId = item.Id, ChatId = Owner.Value, Locale = "en" });
        await db.SaveChangesAsync(Token);
        return (item.Id, analysis.Id);
    }

    private async Task<int> CountTargetsAsync(OwnDayDbContext db) => await db.Actions.CountAsync(Token) +
        await db.Projects.CountAsync(Token) + await db.WaitingFors.CountAsync(Token) +
        await db.References.CountAsync(Token) + await db.SomedayMaybes.CountAsync(Token);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static readonly InboxProposalProperties Empty = new()
    {
        Source = null, WaitingSince = null, WaitingSinceEvidence = null, UnresolvedDateText = null
    };
    private static InboxProposal Proposal(InboxTargetKind kind = InboxTargetKind.Action) => new()
    {
        Kind = kind, Title = "Запись", Description = null, Properties = Empty, ProjectIds = [], Ambiguities = []
    };
    private static InboxProposal[] Composite() =>
    [
        Proposal() with { Title = "Купить молоко" }, Proposal() with { Title = "Позвонить Ивану" },
        Proposal(InboxTargetKind.SomedayMaybe) with { Title = "Попробовать изучить японский" }
    ];

    private sealed class FailInsert(string table) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO " + table, StringComparison.Ordinal))
            {
                Fired = true;
                throw new InvalidOperationException("Simulated storage failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class LostCommitAcknowledgement : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Simulated acknowledgement loss.");
    }

    private sealed class ObserveInboxLock : DbCommandInterceptor
    {
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM inbox_items", StringComparison.Ordinal) &&
                command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                Attempted.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PauseAfterProjectRead : DbCommandInterceptor
    {
        public TaskCompletionSource Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR SHARE", StringComparison.Ordinal))
            {
                Read.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class FakeAnalyzer : IInboxAnalyzer
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public InboxAnalyzerDescriptor Descriptor => new("fake", "inbox-v1", "test");
        public Task<InboxAnalyzerResponse> AnalyzeAsync(InboxAnalysisRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new InboxAnalyzerResponse(JsonSerializer.Serialize(
                new InboxAnalysisOutput { Items = Composite() }, InboxAnalysisJson.Options), "fake"));
        }
    }

    private sealed class WorkerHost(string connection, IInboxAnalyzer ai, bool analysisEnabled) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Default", connection);
            builder.UseSetting("Telegram:BotToken", "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi");
            builder.UseSetting("Telegram:BotUsername", "OwnDayBot");
            builder.UseSetting("Telegram:WebhookSecret", "test-webhook-secret");
            builder.UseSetting("InboxAnalysis:OpenAI:Enabled", analysisEnabled.ToString());
            builder.UseSetting("InboxAnalysis:OpenAI:ApiKey", analysisEnabled ? "dummy-test-key" : "");
            builder.UseSetting("InboxAnalysis:Background:Enabled", analysisEnabled.ToString());
            builder.UseSetting("InboxAnalysis:Background:PollSeconds", "1");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddHostedService<InboxAnalysisWorker>();
                services.RemoveAll<IInboxAnalyzer>();
                services.AddSingleton(ai);
            });
        }
    }
}
