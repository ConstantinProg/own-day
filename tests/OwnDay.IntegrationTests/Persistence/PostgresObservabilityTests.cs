using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

[Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
public sealed class PostgresObservabilityTests : IAsyncLifetime
{
    private readonly string _schema = "ownday_test_" + Guid.NewGuid().ToString("N");
    private DbContextOptions<OwnDayDbContext> _options = null!;
    private string _connectionString = null!;
    private OwnDayDbContext _admin = null!;
    private Guid _analysisId;
    private long _itemId;
    private static readonly UserId Owner = new(101);

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required for this test.");
        _admin = new(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connection).Options);
#pragma warning disable EF1003 // The isolated schema name is generated from a GUID.
        await _admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + _schema);
#pragma warning restore EF1003
        _connectionString = new NpgsqlConnectionStringBuilder(connection) { SearchPath = _schema }.ConnectionString;
        _options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(_connectionString).Options;
        await using var db = new OwnDayDbContext(_options);
        await db.Database.MigrateAsync();
        var item = InboxItem.Capture(Owner, "PRIVATE", DateTime.UtcNow.AddMinutes(-1));
        db.InboxItems.Add(item);
        await db.SaveChangesAsync();
        _itemId = item.Id;
        var analysis = InboxAnalysis.Start(Owner, item.Id, 1, new("fake", "inbox-v1", "test"),
            "{}", "x", "x", item.CapturedAt);
        db.InboxAnalyses.Add(analysis);
        await db.SaveChangesAsync();
        _analysisId = analysis.Id;
    }

    public async Task DisposeAsync()
    {
#pragma warning disable EF1003 // Same isolated GUID schema, never a user-supplied path.
        await _admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + _schema + " CASCADE");
#pragma warning restore EF1003
        await _admin.DisposeAsync();
        using var pool = new NpgsqlConnection(_connectionString);
        NpgsqlConnection.ClearPool(pool);
    }

    [Fact]
    public async Task AdmitAsync_ConcurrentRequestsAndRestart_PreservesReservationsAndPreventsOversubscription()
    {
        var settings = new OpenAICostOptions { TokenLimit = 200, TokensPerRequest = 100 };
        using (var provider = Provider())
        {
            var ledger = Ledger(provider, settings);
            var admissions = await Task.WhenAll(Enumerable.Range(0, 12)
                .Select(_ => ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), CancellationToken.None)));
            Assert.Equal(2, admissions.Count(a => a is not null));
        }
        using var restarted = Provider();
        var afterRestart = Ledger(restarted, settings);
        Assert.Null(await afterRestart.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), CancellationToken.None));
        await using var verify = new OwnDayDbContext(_options);
        Assert.Equal(200, await verify.OpenAIRequestAttempts.SumAsync(a => a.ChargedTokens));
        Assert.All(await verify.OpenAIRequestAttempts.ToListAsync(), a => Assert.Null(a.FinishedAt));
        Assert.Equal(InboxItemStatus.Active, (await verify.InboxItems.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinishAsync_ConcurrentDuplicateCompletion_ReconcilesExactlyOnceAndSnapshotsCachedPrice()
    {
        var settings = new OpenAICostOptions
        {
            TokenLimit = 200, TokensPerRequest = 100,
            Models = new() { ["actual"] = new() { InputUsdPerMillion = 2, CachedInputUsdPerMillion = 0.5m, OutputUsdPerMillion = 8 } }
        };
        using var provider = Provider();
        var ledger = Ledger(provider, settings);
        var id = (await ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), CancellationToken.None))!.Value;
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ledger.FinishAsync(id, "response",
            new("{}", "actual", "provider-safe-id", 30, 10, 20), CancellationToken.None)));
        settings.Models["actual"].InputUsdPerMillion = 999;
        await using var verify = new OwnDayDbContext(_options);
        var row = await verify.OpenAIRequestAttempts.SingleAsync();
        Assert.Equal(40, row.ChargedTokens);
        Assert.Equal(0.00011m, row.EstimatedUsd);
        Assert.Equal(2, row.InputUsdPerMillion);
        Assert.Equal(20, row.CachedInputTokens);
        Assert.NotNull(await ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnalyzeAsync_RefusedOrIncompleteResponse_RetainsUsageAndChargesEveryRetry(bool refusal)
    {
        using var provider = Provider();
        var settings = new OpenAICostOptions { TokenLimit = 160, TokensPerRequest = 100 };
        var ledger = Ledger(provider, settings);
        var inner = new FakeAnalyzer(refusal ? InboxAnalysisCode.Refused : InboxAnalysisCode.IncompleteResponse);
        var analyzer = new CostControlledInboxAnalyzer(inner, ledger);
        await Assert.ThrowsAsync<InboxAnalyzerException>(() => analyzer.AnalyzeAsync(Request(), CancellationToken.None));
        await Assert.ThrowsAsync<InboxAnalyzerException>(() => analyzer.AnalyzeAsync(Request(), CancellationToken.None));
        // Two known 40-token calls leave too little room for another 100-token reservation.
        var blocked = await Assert.ThrowsAsync<InboxAnalyzerException>(() => analyzer.AnalyzeAsync(Request(), CancellationToken.None));
        Assert.Equal(InboxAnalysisCode.BudgetLimit, blocked.Code);
        Assert.Equal(2, inner.Calls);
        await using var verify = new OwnDayDbContext(_options);
        var rows = await verify.OpenAIRequestAttempts.OrderBy(a => a.Sequence).ToListAsync();
        Assert.Equal(new[] { 1, 2 }, rows.Select(a => a.Sequence));
        Assert.All(rows, a =>
        {
            Assert.Equal(30, a.InputTokens);
            Assert.Equal(20, a.CachedInputTokens);
            Assert.Null(a.EstimatedUsd); // Unknown actual model pricing must not become zero.
        });
        var stats = await new EfInboxStatisticsStore(verify).ReadAsync(Owner,
            StatisticsPeriod.Parse("all", DateTime.UtcNow.AddSeconds(1))!, CancellationToken.None);
        Assert.Equal(2, stats.ProviderAttempts);
        Assert.Equal(1, stats.Retries);
        Assert.Equal(2, stats.ProviderErrors);
        Assert.Equal(2, stats.Tokens.UnknownCost);
        Assert.Equal(60, stats.Tokens.Input);
        Assert.Equal(40, stats.Tokens.Cached);
    }

    [Fact]
    public async Task AnalyzeAsync_CancellationAfterAdmission_RetainsUnknownReservationAcrossRestart()
    {
        using var provider = Provider();
        var settings = new OpenAICostOptions { TokenLimit = 100, TokensPerRequest = 100 };
        var ledger = Ledger(provider, settings);
        using var cancellation = new CancellationTokenSource();
        var analyzer = new CostControlledInboxAnalyzer(new CancellingAnalyzer(cancellation), ledger);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync(Request(), cancellation.Token));
        using var restarted = Provider();
        Assert.Null(await Ledger(restarted, settings).AdmitAsync(Request(), analyzer.Descriptor, CancellationToken.None));
        await using var db = new OwnDayDbContext(_options);
        var row = await db.OpenAIRequestAttempts.SingleAsync();
        Assert.Null(row.FinishedAt);
        Assert.Null(row.InputTokens);
        Assert.Equal(100, row.ChargedTokens);
    }

    [Fact]
    public async Task AdmitAsync_ForeignOwner_RejectsWithoutReservingBudget()
    {
        using var provider = Provider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ledger(provider, new()).AdmitAsync(
            Request() with { OwnerId = 102 }, new("fake", "inbox-v1", "test"), CancellationToken.None));
        await using var db = new OwnDayDbContext(_options);
        Assert.Empty(await db.OpenAIRequestAttempts.ToListAsync());
    }

    [Fact]
    public async Task MigrateAsync_ExistingUndoAndManualTargets_BackfillsCreationFactsWithoutInventingProviderUsage()
    {
        await using (var old = new OwnDayDbContext(_options))
        {
            await old.GetService<IMigrator>().MigrateAsync("20261025180000_AddInboxUndo");
            var now = DateTime.UtcNow;
            old.InboxAutomaticApplications.Add(new InboxAutomaticApplication
            {
                AnalysisId = _analysisId, InboxItemId = _itemId, UserId = Owner,
                PolicyVersion = "inbox-policy-v2", EvaluatedAt = now,
                Route = OwnDay.Application.Inbox.Policy.InboxPolicyRoute.AutoApply,
                TargetsJson = """[{"proposalId":"00000000-0000-0000-0000-000000000001","kind":"Action","targetId":1234,"projectId":null}]""",
                UndoneAt = now
            });
            var manual = InboxItem.Capture(Owner, "PRIVATE MANUAL", now.AddSeconds(-1));
            manual.Process(InboxTargetKind.Reference, 777, now);
            old.InboxItems.Add(manual);
            await old.SaveChangesAsync();
            await old.Database.MigrateAsync();
        }
        await using var verify = new OwnDayDbContext(_options);
        var events = await verify.InboxOperationEvents.ToListAsync();
        Assert.Equal(2, events.Count(e => e.Code == "created"));
        Assert.Single(events, e => e.Code == "created" && e.Kind == InboxTargetKind.Action && e.TargetId == 1234);
        Assert.Single(events, e => e.Code == "created" && e.Kind == InboxTargetKind.Reference && e.TargetId == 777);
        Assert.Single(events, e => e.Code == "manual");
        Assert.Empty(await verify.OpenAIRequestAttempts.ToListAsync());
    }

    [Fact]
    public async Task ProcessJobAsync_BudgetExhausted_DoesNotCallProviderOrLoseInboxAfterRecovery()
    {
        using var provider = Provider();
        var settings = new OpenAICostOptions { TokenLimit = 100, TokensPerRequest = 100 };
        var ledger = Ledger(provider, settings);
        Assert.NotNull(await ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), CancellationToken.None));
        await using (var db = new OwnDayDbContext(_options))
        {
            db.InboxAnalysisJobs.Add(InboxAnalysisJob.Schedule(await db.InboxItems.SingleAsync()));
            await db.SaveChangesAsync();
            var jobs = new EfInboxAnalysisJobStore(db, new EfInboxProcessingStore(db));
            var lease = (await jobs.ClaimAsync(TimeSpan.FromMinutes(2), CancellationToken.None))!;
            var inner = new FakeAnalyzer(InboxAnalysisCode.ProviderFailure);
            var service = new InboxAnalysisService(new EfInboxItemStore(db),
                new EfInboxAnalysisStore(db, new EfInboxProcessingStore(db)), new CostControlledInboxAnalyzer(inner, ledger),
                new(), TimeProvider.System);
            Assert.Equal("budget_limit", await service.ProcessJobAsync(lease, jobs, new(), CancellationToken.None));
            Assert.Equal(0, inner.Calls);
        }
        await using var restarted = new OwnDayDbContext(_options);
        await new EfInboxAnalysisJobStore(restarted, new EfInboxProcessingStore(restarted)).RecoverAsync(true, CancellationToken.None);
        Assert.Equal(InboxItemStatus.Active, (await restarted.InboxItems.SingleAsync()).Status);
        Assert.Equal(_itemId, (await restarted.InboxItems.SingleAsync()).Id);
        Assert.Equal(InboxAnalysisJobStatus.Failed, (await restarted.InboxAnalysisJobs.SingleAsync()).Status);
        Assert.Single(await restarted.InboxOperationEvents.Where(e => e.Code == "background_budget_limit_error").ToListAsync());
        Assert.Single(await restarted.OpenAIRequestAttempts.ToListAsync());
    }

    private InboxAnalysisRequest Request() => new("PRIVATE", DateTime.UtcNow, new(null, [], false), [], _analysisId, Owner.Value);

    private ServiceProvider Provider() => new ServiceCollection()
        .AddScoped(_ => new OwnDayDbContext(_options)).BuildServiceProvider();

    private static EfOpenAIRequestLedger Ledger(IServiceProvider provider, OpenAICostOptions options) => new(
        provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), TimeProvider.System,
        NullLogger<EfOpenAIRequestLedger>.Instance);

    private sealed class FakeAnalyzer(InboxAnalysisCode code) : IInboxAnalyzer
    {
        public int Calls { get; private set; }
        public InboxAnalyzerDescriptor Descriptor => new("fake", "inbox-v1", "test");
        public Task<InboxAnalyzerResponse> AnalyzeAsync(InboxAnalysisRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InboxAnalyzerException(code, new("", "actual", "safe-id", 30, 10, 20));
        }
    }

    private sealed class CancellingAnalyzer(CancellationTokenSource cancellation) : IInboxAnalyzer
    {
        public InboxAnalyzerDescriptor Descriptor => new("fake", "inbox-v1", "test");
        public Task<InboxAnalyzerResponse> AnalyzeAsync(InboxAnalysisRequest request, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        }
    }
}
