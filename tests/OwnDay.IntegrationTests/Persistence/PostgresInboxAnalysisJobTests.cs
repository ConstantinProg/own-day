using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.OpenAIAnalysis;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Cleanup;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

[Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
public sealed class PostgresInboxAnalysisJobTests : IAsyncLifetime
{
    private readonly string _schema = "ownday_test_" + Guid.NewGuid().ToString("N");
    private static readonly UserId Owner = new(101);
    private DbContextOptions<OwnDayDbContext> _options = null!;
    private OwnDayDbContext _admin = null!;
    private string _connection = null!;
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
    private CancellationToken Token => _deadline.Token;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required for this test.");
        _admin = new(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connection).Options);
#pragma warning disable EF1003 // Isolated schema generated from GUID, never user input.
        await _admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + _schema, Token);
#pragma warning restore EF1003
        _connection = new NpgsqlConnectionStringBuilder(connection) { SearchPath = _schema }.ConnectionString;
        _options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(_connection).Options;
        await using var db = Db();
        await db.Database.MigrateAsync(Token);
    }

    public async Task DisposeAsync()
    {
#pragma warning disable EF1003 // Same GUID schema; cleanup token has its own lifetime.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await _admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + _schema + " CASCADE", cleanup.Token);
#pragma warning restore EF1003
        await _admin.DisposeAsync();
        _deadline.Dispose();
    }

    [Fact]
    public async Task CaptureAsync_JobInsertFails_RollsBackItemAndJob()
    {
        await using var db = Db();
        var item = InboxItem.Capture(Owner, "private", DateTime.UtcNow);
        db.InboxAnalysisJobs.Add(new() { Item = item, UserId = Owner, CreatedAt = DateTime.UtcNow, AvailableAt = DateTime.UtcNow });
        // AddAsync must attempt to insert its own job too: the unique constraint rejects the entire SaveChanges.
        await Assert.ThrowsAsync<DbUpdateException>(() => new EfInboxItemStore(db).AddAsync(item, Token));
        await using var reader = Db();
        Assert.Empty(await reader.InboxItems.ToListAsync(Token));
        Assert.Empty(await reader.InboxAnalysisJobs.ToListAsync(Token));
    }

    [Fact]
    public async Task CaptureAsync_OuterTransaction_JobAndItemAreInvisibleUntilCommitAndRollbackTogether()
    {
        await using var db = Db();
        await using var transaction = await db.Database.BeginTransactionAsync(Token);
        await new InboxCaptureService(new EfInboxItemStore(db), TimeProvider.System).CaptureAsync(Owner, "capture", Token);
        Assert.Single(await db.InboxAnalysisJobs.ToListAsync(Token));
        await using var reader = Db();
        Assert.Empty(await reader.InboxItems.ToListAsync(Token));
        Assert.Empty(await reader.InboxAnalysisJobs.ToListAsync(Token));
        await transaction.RollbackAsync(Token);
        Assert.Empty(await reader.InboxItems.ToListAsync(Token));
        Assert.Empty(await reader.InboxAnalysisJobs.ToListAsync(Token));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public async Task Post_DisabledOrMissingKey_CapturesOnceWithoutAnalyzer(bool enabled, bool key, bool background)
    {
        var ai = new FakeAnalyzer((_, _) => throw new InvalidOperationException("No provider calls allowed"));
        using var host = new HostFactory(_connection, ai, enabled, key, background);
        using var client = host.CreateClient();
        await PostAsync(client);
        await PostAsync(client);
        await using var db = Db();
        Assert.Single(await db.InboxItems.ToListAsync(Token));
        var job = Assert.Single(await db.InboxAnalysisJobs.ToListAsync(Token));
        Assert.Equal(InboxAnalysisJobStatus.Ready, job.Status);
        Assert.Equal(0, ai.Calls);
        Assert.Single(await db.ProcessedTelegramUpdates.ToListAsync(Token));
        Assert.Single(await db.TelegramOutboxMessages.ToListAsync(Token));
    }

    [Fact]
    public async Task Post_HostStopsDuringAnalysis_RestartRecoversSameVersionWithoutBlockingWebhook()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = new FakeAnalyzer(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        using (var host = new HostFactory(_connection, ai))
        using (var client = host.CreateClient())
        {
            await PostAsync(client); // Would time out if the webhook awaited the provider.
            await started.Task.WaitAsync(Token);
        }

        Guid id;
        await using (var db = Db())
        {
            id = (await db.InboxAnalyses.SingleAsync(Token)).Id;
            Assert.Equal(InboxAnalysisStatus.Pending, (await db.InboxAnalyses.SingleAsync(Token)).Status);
        }

        await MakeReadyAsync();
        var success = new FakeAnalyzer((_, _) => Task.FromResult(Response()));
        using (var host = new HostFactory(_connection, success))
        using (host.CreateClient())
        {
            await WaitForAsync(async () =>
            {
                await using var db = Db();
                return await db.InboxAnalysisJobs.AnyAsync(row => row.Status == InboxAnalysisJobStatus.Completed, Token);
            });
        }

        await using var verify = Db();
        Assert.Equal(id, Assert.Single(await verify.InboxAnalyses.ToListAsync(Token)).Id);
        Assert.Single(await verify.Set<InboxAnalysisProposal>().ToListAsync(Token));
        Assert.Equal(1, success.Calls);
    }

    [Fact]
    public async Task ClaimAsync_ConcurrentWorkers_OnlyOneOwnerAndOneResult()
    {
        await CaptureAsync();
        var leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = Db();
            return await Jobs(db).ClaimAsync(TimeSpan.FromMinutes(2), Token);
        }));
        var lease = Assert.Single(leases, row => row is not null)!;
        var ai = new FakeAnalyzer((_, _) => Task.FromResult(Response()));
        await using (var db = Db())
        {
            Assert.Equal("validated", await Service(db, ai).ProcessJobAsync(lease, Jobs(db), new(), Token));
        }

        await using var verify = Db();
        Assert.Null(await Jobs(verify).FinishAsync(lease, new("validated"), null, Token));
        Assert.Null(await Jobs(verify).ClaimAsync(TimeSpan.FromMinutes(2), Token));
        Assert.Equal(1, ai.Calls);
        Assert.Single(await verify.InboxAnalyses.ToListAsync(Token));
        Assert.Single(await verify.Set<InboxAnalysisProposal>().ToListAsync(Token));
        Assert.Equal(InboxItemStatus.Active, (await verify.InboxItems.SingleAsync(Token)).Status);
        Assert.Empty(await verify.Actions.ToListAsync(Token));
        Assert.Empty(await verify.Projects.ToListAsync(Token));
        Assert.Empty(await verify.WaitingFors.ToListAsync(Token));
        Assert.Empty(await verify.References.ToListAsync(Token));
        Assert.Empty(await verify.SomedayMaybes.ToListAsync(Token));
    }

    [Theory]
    [InlineData("provider_failure")]
    [InlineData("rate_limit")]
    [InlineData("timeout")]
    public async Task ProcessJobAsync_TransientFailure_RetriesSameVersionWithBackoff(string code)
    {
        await CaptureAsync();
        var calls = 0;
        var ai = new FakeAnalyzer((_, _) => Interlocked.Increment(ref calls) == 1
            ? throw new InboxAnalyzerException(code) : Task.FromResult(Response()));
        await RunAsync(ai);
        await using (var db = Db())
        {
            var job = await db.InboxAnalysisJobs.SingleAsync(Token);
            Assert.Equal(InboxAnalysisJobStatus.Ready, job.Status);
            Assert.InRange((job.AvailableAt - DateTime.UtcNow).TotalSeconds, 10, 31);
            Assert.Null(await Jobs(db).ClaimAsync(TimeSpan.FromMinutes(2), Token));
            Assert.Equal(InboxAnalysisStatus.Pending, (await db.InboxAnalyses.SingleAsync(Token)).Status);
        }

        await MakeReadyAsync();
        await RunAsync(ai);
        await using var verify = Db();
        Assert.Equal(1, Assert.Single(await verify.InboxAnalyses.ToListAsync(Token)).Version);
        Assert.Equal(InboxAnalysisJobStatus.Completed, (await verify.InboxAnalysisJobs.SingleAsync(Token)).Status);
        Assert.Equal(2, ai.Calls);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("incomplete_response")]
    [InlineData("context_limit")]
    [InlineData("configuration")]
    [InlineData("budget_limit")]
    [InlineData("permanent_failure")]
    [InlineData("invalid_response")]
    public async Task ProcessJobAsync_PermanentFailure_StopsWithoutChangingInbox(string code)
    {
        await CaptureAsync();
        var ai = new FakeAnalyzer((_, _) => throw new InboxAnalyzerException(code));
        await RunAsync(ai);
        await using var db = Db();
        var job = await db.InboxAnalysisJobs.SingleAsync(Token);
        Assert.Equal(InboxAnalysisJobStatus.Failed, job.Status);
        Assert.Equal(code, job.LastCode);
        Assert.Null(await Jobs(db).ClaimAsync(TimeSpan.FromMinutes(2), Token));
        Assert.NotEqual(InboxAnalysisStatus.Pending, (await db.InboxAnalyses.SingleAsync(Token)).Status);
        Assert.Equal(InboxItemStatus.Active, (await db.InboxItems.SingleAsync(Token)).Status);
        Assert.Equal(1, ai.Calls);
    }

    [Theory]
    [InlineData("other-model", "v1", "v1")]
    [InlineData("fake", "v2", "v1")]
    [InlineData("fake", "v1", "v2")]
    public async Task ProcessJobAsync_DescriptorChangesBetweenAttempts_TerminatesSameVersionWithoutProviderCall(
        string model, string schema, string prompt)
    {
        await CaptureAsync();
        await RunAsync(new FakeAnalyzer((_, _) => throw new InboxAnalyzerException("provider_failure")));
        Guid analysisId;
        await using (var db = Db()) { analysisId = (await db.InboxAnalyses.SingleAsync(Token)).Id; }

        await MakeReadyAsync();
        var ai = new FakeAnalyzer((_, _) => Task.FromResult(Response())) { Descriptor = new(model, schema, prompt) };
        await RunAsync(ai);

        await using var verify = Db();
        var job = await verify.InboxAnalysisJobs.SingleAsync(Token);
        var analysis = Assert.Single(await verify.InboxAnalyses.ToListAsync(Token));
        Assert.Equal(InboxAnalysisJobStatus.Failed, job.Status);
        Assert.Equal("descriptor_mismatch", job.LastCode);
        Assert.Equal(analysisId, job.AnalysisId);
        Assert.Equal(analysisId, analysis.Id);
        Assert.Equal(1, analysis.Version);
        Assert.Equal(InboxAnalysisStatus.Failed, analysis.Status);
        Assert.NotNull(analysis.CompletedAt);
        using var diagnostics = JsonDocument.Parse(analysis.DiagnosticsJson);
        Assert.Equal("descriptor_mismatch", diagnostics.RootElement.GetProperty("code").GetString());
        Assert.Equal(new("fake", "v1", "v1"), new InboxAnalyzerDescriptor(analysis.Model, analysis.SchemaVersion, analysis.PromptVersion));
        Assert.Empty(await verify.Set<InboxAnalysisProposal>().ToListAsync(Token));
        Assert.Null(await Jobs(verify).ClaimAsync(TimeSpan.FromMinutes(2), Token));
        Assert.Equal(0, ai.Calls);
        Assert.Equal(InboxItemStatus.Active, (await verify.InboxItems.SingleAsync(Token)).Status);
    }

    [Theory]
    [InlineData("low", 8192, 60, false, true)]
    [InlineData("high", 8192, 60, false, false)]
    [InlineData("low", 4096, 60, false, false)]
    [InlineData("low", 8192, 30, false, false)]
    [InlineData("low", 8192, 60, true, false)]
    public async Task ProcessJobAsync_RestartWithParameters_PreservesSnapshotAndFencesChangedConfiguration(
        string effort, int tokens, int timeout, bool legacy, bool succeeds)
    {
        await CaptureAsync();
        var original = legacy ? new InboxAnalyzerDescriptor("gpt-6-luna", "v1", "v1")
            : new("gpt-6-luna", "v1", "v1", "low", 8192, 60);
        await RunAsync(new FakeAnalyzer((_, _) => throw new InboxAnalyzerException("provider_failure"))
        {
            Descriptor = original
        });
        Guid analysisId;
        await using (var db = Db())
        {
            var pending = await db.InboxAnalyses.SingleAsync(Token);
            analysisId = pending.Id;
            Assert.Equal(original.ReasoningEffort, pending.ReasoningEffort);
            Assert.Equal(original.MaxOutputTokens, pending.MaxOutputTokens);
            Assert.Equal(original.TimeoutSeconds, pending.TimeoutSeconds);
        }

        await MakeReadyAsync();
        var ai = new FakeAnalyzer((_, _) => Task.FromResult(Response() with { Model = "gpt-6-luna-resolved" }))
        {
            Descriptor = new("gpt-6-luna", "v1", "v1", effort, tokens, timeout)
        };
        await RunAsync(ai);

        await using var verify = Db();
        var job = await verify.InboxAnalysisJobs.SingleAsync(Token);
        var analysis = Assert.Single(await verify.InboxAnalyses.ToListAsync(Token));
        Assert.Equal(analysisId, analysis.Id);
        Assert.Equal(1, analysis.Version);
        Assert.Equal(original.ReasoningEffort, analysis.ReasoningEffort);
        Assert.Equal(original.MaxOutputTokens, analysis.MaxOutputTokens);
        Assert.Equal(original.TimeoutSeconds, analysis.TimeoutSeconds);
        Assert.Equal(succeeds ? "gpt-6-luna-resolved" : original.Model, analysis.Model);
        Assert.Equal(succeeds ? InboxAnalysisJobStatus.Completed : InboxAnalysisJobStatus.Failed, job.Status);
        Assert.Equal(succeeds ? "validated" : "descriptor_mismatch", job.LastCode);
        Assert.Equal(succeeds ? 1 : 0, ai.Calls);
        Assert.Equal(succeeds ? 1 : 0, await verify.Set<InboxAnalysisProposal>().CountAsync(Token));
        Assert.Null(await Jobs(verify).ClaimAsync(TimeSpan.FromMinutes(2), Token));
    }

    [Fact]
    public async Task ProcessJobAsync_ExhaustedRetries_TerminatesPendingVersion()
    {
        await CaptureAsync();
        var ai = new FakeAnalyzer((_, _) => throw new InboxAnalyzerException("provider_failure"));
        var options = new InboxAnalysisProcessingOptions { MaxAttempts = 2 };
        await RunAsync(ai, options);
        await MakeReadyAsync();
        await RunAsync(ai, options);
        await using var db = Db();
        Assert.Equal(InboxAnalysisJobStatus.Failed, (await db.InboxAnalysisJobs.SingleAsync(Token)).Status);
        Assert.Equal(InboxAnalysisStatus.Failed, Assert.Single(await db.InboxAnalyses.ToListAsync(Token)).Status);
        Assert.Equal(2, ai.Calls);
    }

    [Fact]
    public async Task ProcessJobAsync_HungAdapter_TimeoutTerminatesAndLateResponseCannotSave()
    {
        await CaptureAsync();
        var response = new TaskCompletionSource<InboxAnalyzerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = new FakeAnalyzer((_, _) => response.Task);
        await RunAsync(ai, new() { TimeoutSeconds = 1, MaxAttempts = 1 });
        response.SetResult(Response());
        await using var db = Db();
        Assert.Equal(InboxAnalysisStatus.Failed, (await db.InboxAnalyses.SingleAsync(Token)).Status);
        Assert.Equal("timeout", (await db.InboxAnalysisJobs.SingleAsync(Token)).LastCode);
        Assert.Empty(await db.Set<InboxAnalysisProposal>().ToListAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessJobAsync_DiscardBeforeOrDuringNetwork_SkipsWithoutDomainMutation(bool during)
    {
        var item = await CaptureAsync();
        async Task DiscardAsync()
        {
            await using var db = Db();
            await new InboxProcessingService(new EfInboxProcessingStore(db), new EfActionStore(db),
                    new EfStructuredItemStore(db), TimeProvider.System)
                .DiscardAsync(Owner, item.Id, Token);
        }

        if (!during) { await DiscardAsync(); }
        var ai = new FakeAnalyzer(async (_, _) => { await DiscardAsync(); return Response(); });
        await RunAsync(ai);
        await using var verify = Db();
        Assert.Equal(InboxAnalysisJobStatus.Skipped, (await verify.InboxAnalysisJobs.SingleAsync(Token)).Status);
        Assert.Empty(await verify.Set<InboxAnalysisProposal>().ToListAsync(Token));
        Assert.Equal(during ? 1 : 0, ai.Calls);
        Assert.Equal(InboxItemStatus.Discarded, (await verify.InboxItems.SingleAsync(Token)).Status);
        Assert.DoesNotContain(await verify.InboxAnalyses.ToListAsync(Token), row => row.Status == InboxAnalysisStatus.Pending);
    }

    [Fact]
    public async Task FinishAsync_ExpiredLease_FencesStaleOwnerAndReusesPendingAnalysis()
    {
        await CaptureAsync();
        InboxAnalysisLease old;
        Guid id;
        await using (var db = Db())
        {
            old = (await Jobs(db).ClaimAsync(TimeSpan.FromMinutes(2), Token))!;
            id = (await Jobs(db).StartAsync(old, new("fake", "v1", "v1"),
                JsonSerializer.Serialize(new InboxAnalysisContext(null, [], false), InboxAnalysisJson.Options), "hash", "hash", Token))!.Id;
        }

        await MakeReadyAsync();
        InboxAnalysisLease current;
        await using (var db = Db()) { current = (await Jobs(db).ClaimAsync(TimeSpan.FromMinutes(2), Token))!; }
        Assert.NotEqual(old.Token, current.Token);
        await using (var stale = Db())
        {
            Assert.Null(await Jobs(stale).FinishAsync(old, new("permanent_failure"), null, Token));
        }

        var ai = new FakeAnalyzer((_, _) => Task.FromResult(Response()));
        await using (var db = Db()) { await Service(db, ai).ProcessJobAsync(current, Jobs(db), new(), Token); }
        await using var verify = Db();
        Assert.Equal(id, Assert.Single(await verify.InboxAnalyses.ToListAsync(Token)).Id);
        Assert.Single(await verify.Set<InboxAnalysisProposal>().ToListAsync(Token));
    }

    [Fact]
    public async Task FinishAsync_CommitFailsAfterResultWrites_RollsBackResultAndRecoversWithoutDuplicates()
    {
        await CaptureAsync();
        await using (var db = Db())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_completion() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF NEW.status = 2 THEN RAISE EXCEPTION 'test failure'; END IF; RETURN NEW; END $$;
                CREATE CONSTRAINT TRIGGER reject_completion AFTER UPDATE ON inbox_analysis_jobs
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION reject_completion();
                """, Token);
        }

        var ai = new FakeAnalyzer((_, _) => Task.FromResult(Response()));
        await Assert.ThrowsAsync<PostgresException>(() => RunAsync(ai));
        await using (var db = Db())
        {
            Assert.Equal(InboxAnalysisStatus.Pending, (await db.InboxAnalyses.SingleAsync(Token)).Status);
            Assert.Empty(await db.Set<InboxAnalysisProposal>().ToListAsync(Token));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_completion ON inbox_analysis_jobs", Token);
        }

        await MakeReadyAsync();
        await RunAsync(ai);
        await using var verify = Db();
        Assert.Equal(InboxAnalysisStatus.Completed, Assert.Single(await verify.InboxAnalyses.ToListAsync(Token)).Status);
        Assert.Single(await verify.Set<InboxAnalysisProposal>().ToListAsync(Token));
    }

    [Fact]
    public async Task RecoverAsync_InterruptedManualAndDisabledAutomaticAttempts_TerminatesPendingHistory()
    {
        var item = await CaptureAsync();
        await using (var db = Db())
        {
            db.InboxAnalyses.Add(InboxAnalysis.Start(Owner, item.Id, 1, new("fake", "v1", "v1"), "{}", "hash", "hash",
                DateTime.UtcNow.AddHours(-1)));
            await db.SaveChangesAsync(Token);
            var lease = (await Jobs(db).ClaimAsync(TimeSpan.FromMinutes(2), Token))!;
            await Jobs(db).StartAsync(lease, new("fake", "v1", "v1"), "{}", "hash", "hash", Token);
        }

        await MakeReadyAsync();
        await using var recover = Db();
        await Jobs(recover).RecoverAsync(false, Token);
        Assert.All(await recover.InboxAnalyses.ToListAsync(Token), row => Assert.Equal(InboxAnalysisStatus.Failed, row.Status));
        Assert.Equal(InboxAnalysisJobStatus.Failed, (await recover.InboxAnalysisJobs.SingleAsync(Token)).Status);
    }

    [Fact]
    public async Task ProcessJobAsync_CrashConsumesLastAttempt_TerminatesWithoutAnotherProviderCall()
    {
        await CaptureAsync();
        await using (var db = Db())
        {
            var lease = (await Jobs(db).ClaimAsync(TimeSpan.FromMinutes(2), Token))!;
            await Jobs(db).StartAsync(lease, new("fake", "v1", "v1"), "{}", "hash", "hash", Token);
        }

        await MakeReadyAsync();
        var ai = new FakeAnalyzer((_, _) => throw new InvalidOperationException("Budget exhausted"));
        await RunAsync(ai, new() { MaxAttempts = 1 });
        await using var verify = Db();
        Assert.Equal(InboxAnalysisStatus.Failed, (await verify.InboxAnalyses.SingleAsync(Token)).Status);
        Assert.Equal("attempts_exhausted", (await verify.InboxAnalysisJobs.SingleAsync(Token)).LastCode);
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task ProcessJobAsync_ResultAlreadySaved_CompletesJobWithoutCallingProvider()
    {
        await CaptureAsync();
        await using (var db = Db())
        {
            var lease = (await Jobs(db).ClaimAsync(TimeSpan.FromMinutes(2), Token))!;
            var analysis = (await Jobs(db).StartAsync(lease, new("fake", "v1", "v1"), "{}", "hash", "hash", Token))!;
            // Simulate an old/non-atomic writer or recovery of a result committed before the job acknowledgement.
            analysis.Finish(InboxAnalysisStatus.Completed, DateTime.UtcNow, "{}",
                JsonSerializer.Deserialize<InboxAnalysisOutput>(Response().Json, InboxAnalysisJson.Options));
            await db.SaveChangesAsync(Token);
        }

        await MakeReadyAsync();
        var ai = new FakeAnalyzer((_, _) => throw new InvalidOperationException("Result already exists"))
        {
            Descriptor = new("other-model", "v2", "v2")
        };
        await RunAsync(ai);
        await using var verify = Db();
        Assert.Equal(InboxAnalysisJobStatus.Completed, (await verify.InboxAnalysisJobs.SingleAsync(Token)).Status);
        Assert.Single(await verify.InboxAnalyses.ToListAsync(Token));
        Assert.Single(await verify.Set<InboxAnalysisProposal>().ToListAsync(Token));
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task ProcessJobAsync_PostgresConnectionLostAfterProviderResponse_LeaseRecoversSameVersion()
    {
        await CaptureAsync();
        await using (var db = Db())
        {
            var ai = new FakeAnalyzer(async (_, token) =>
            {
                Assert.Null(db.Database.CurrentTransaction);
                await db.Database.OpenConnectionAsync(token);
                var processId = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
                await using var killer = Db();
                await killer.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_terminate_backend({processId})", token);
                return Response();
            });
            var lease = (await Jobs(db).ClaimAsync(TimeSpan.FromMinutes(2), Token))!;
            await Assert.ThrowsAnyAsync<Exception>(() => Service(db, ai).ProcessJobAsync(lease, Jobs(db), new(), Token));
        }

        await MakeReadyAsync();
        await RunAsync(new FakeAnalyzer((_, _) => Task.FromResult(Response())));
        await using var verify = Db();
        Assert.Equal(InboxAnalysisStatus.Completed, Assert.Single(await verify.InboxAnalyses.ToListAsync(Token)).Status);
        Assert.Single(await verify.Set<InboxAnalysisProposal>().ToListAsync(Token));
    }

    [Fact]
    public async Task ProcessJobAsync_OperatorRunsDuringAutomaticAnalysis_KeepsIndependentRevisionHistory()
    {
        var item = await CaptureAsync();
        var ai = new FakeAnalyzer(async (_, token) =>
        {
            await using var manual = Db();
            var manualAi = new FakeAnalyzer((_, _) => Task.FromResult(Response()));
            var revision = await Service(manual, manualAi).AnalyzeAsync(Owner, item.Id, token);
            Assert.Equal(2, revision!.Version);
            return Response();
        });
        await RunAsync(ai);
        await using var verify = Db();
        var history = await verify.InboxAnalyses.OrderBy(row => row.Version).ToListAsync(Token);
        Assert.Equal(new[] { 1, 2 }, history.Select(row => row.Version));
        Assert.All(history, row => Assert.Equal(InboxAnalysisStatus.Completed, row.Status));
        Assert.Equal(history[0].Id, (await verify.InboxAnalysisJobs.SingleAsync(Token)).AnalysisId);
    }

    [Fact]
    public async Task ProcessJobAsync_ManualProcessingDuringNetwork_SkipsResultAndPreservesTarget()
    {
        var item = await CaptureAsync();
        var ai = new FakeAnalyzer(async (_, token) =>
        {
            await using var manual = Db();
            var result = await new InboxProcessingService(new EfInboxProcessingStore(manual), new EfActionStore(manual),
                new EfStructuredItemStore(manual), TimeProvider.System).ProcessAsync(Owner,
                    new(item.Id, InboxTargetKind.Action, "Manual decision"), token);
            Assert.Equal(ProcessInboxItemStatus.Processed, result.Status);
            return Response();
        });
        await RunAsync(ai);
        await using var verify = Db();
        var job = await verify.InboxAnalysisJobs.SingleAsync(Token);
        Assert.Equal(InboxAnalysisJobStatus.Skipped, job.Status);
        Assert.Equal("item_inactive", job.LastCode);
        Assert.Equal("Manual decision", Assert.Single(await verify.Actions.ToListAsync(Token)).Title);
        Assert.Equal(InboxItemStatus.Processed, (await verify.InboxItems.SingleAsync(Token)).Status);
        Assert.Empty(await verify.Set<InboxAnalysisProposal>().ToListAsync(Token));
    }

    [Fact]
    public async Task ProcessJobAsync_ForeignOrMissingItem_DoesNotCallProviderOrCompleteAnotherUsersJob()
    {
        await CaptureAsync();
        await using var db = Db();
        var lease = (await Jobs(db).ClaimAsync(TimeSpan.FromMinutes(2), Token))!;
        var ai = new FakeAnalyzer((_, _) => throw new InvalidOperationException("Invalid ownership"));
        Assert.Equal("lease_lost", await Service(db, ai).ProcessJobAsync(lease with { UserId = new(102) }, Jobs(db), new(), Token));
        Assert.Equal("lease_lost", await Service(db, ai).ProcessJobAsync(lease with { ItemId = long.MaxValue }, Jobs(db), new(), Token));
        Assert.Equal(0, ai.Calls);
        Assert.Empty(await db.InboxAnalyses.ToListAsync(Token));
    }

    [Fact]
    public async Task ProcessJobAsync_MalformedModelResponse_PersistsTerminalInvalidResponseWithoutRetry()
    {
        await CaptureAsync();
        var ai = new FakeAnalyzer((_, _) => Task.FromResult(new InboxAnalyzerResponse("not-json", "fake")));
        await RunAsync(ai);
        await using var db = Db();
        Assert.Equal(InboxAnalysisStatus.InvalidResponse, (await db.InboxAnalyses.SingleAsync(Token)).Status);
        Assert.Equal(InboxAnalysisJobStatus.Failed, (await db.InboxAnalysisJobs.SingleAsync(Token)).Status);
        Assert.Empty(await db.Set<InboxAnalysisProposal>().ToListAsync(Token));
        Assert.Null(await Jobs(db).ClaimAsync(TimeSpan.FromMinutes(2), Token));
    }

    [Fact]
    public async Task Worker_MultipleReadyJobs_RespectsParallelismLimit()
    {
        for (var index = 0; index < 3; index++) { await CaptureAsync(); }
        var twoRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var ai = new FakeAnalyzer(async (_, token) =>
        {
            var count = Interlocked.Increment(ref active);
            Interlocked.Exchange(ref maximum, Math.Max(count, Volatile.Read(ref maximum)));
            if (count == 2) { twoRunning.TrySetResult(); }
            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref active);
            return Response();
        });
        using var host = new HostFactory(_connection, ai, parallelism: 2);
        using var client = host.CreateClient();
        await twoRunning.Task.WaitAsync(Token);
        await using (var db = Db())
        {
            Assert.Equal(2, await db.InboxAnalysisJobs.CountAsync(row => row.Status == InboxAnalysisJobStatus.Running, Token));
            Assert.Equal(1, await db.InboxAnalysisJobs.CountAsync(row => row.Status == InboxAnalysisJobStatus.Ready, Token));
        }

        release.SetResult();
        await WaitForAsync(async () =>
        {
            await using var db = Db();
            return await db.InboxAnalysisJobs.CountAsync(row => row.Status == InboxAnalysisJobStatus.Completed, Token) == 3;
        });
        Assert.Equal(2, maximum);
        Assert.Equal(3, ai.Calls);
    }

    private OwnDayDbContext Db() => new(_options);
    private static EfInboxAnalysisJobStore Jobs(OwnDayDbContext db) => new(db, new EfInboxProcessingStore(db));
    private static InboxAnalysisService Service(OwnDayDbContext db, IInboxAnalyzer ai) => new(new EfInboxItemStore(db),
        new EfInboxAnalysisStore(db, new EfInboxProcessingStore(db)), ai, new(), TimeProvider.System);
    private async Task<InboxItem> CaptureAsync()
    {
        await using var db = Db();
        return await new EfInboxItemStore(db).AddAsync(InboxItem.Capture(Owner, "private capture", DateTime.UtcNow), Token);
    }

    private async Task RunAsync(IInboxAnalyzer ai, InboxAnalysisProcessingOptions? options = null)
    {
        await using var db = Db();
        var lease = await Jobs(db).ClaimAsync(TimeSpan.FromMinutes(2), Token);
        Assert.NotNull(lease);
        await Service(db, ai).ProcessJobAsync(lease, Jobs(db), options ?? new(), Token);
    }

    private async Task MakeReadyAsync()
    {
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE inbox_analysis_jobs SET available_at = clock_timestamp() - interval '1 second',
                lease_until = CASE WHEN status = 1 THEN clock_timestamp() - interval '1 second' ELSE NULL END
            """, Token);
    }

    private async Task WaitForAsync(Func<Task<bool>> predicate)
    {
        while (!await predicate()) { await Task.Delay(50, Token); }
    }

    private async Task PostAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "test-webhook-secret");
        request.Content = new StringContent("""
            {"update_id":1001,"message":{"message_id":1001,"date":1790244000,
            "from":{"id":101,"is_bot":false,"first_name":"Test"},
            "chat":{"id":101,"type":"private"},"text":"private capture"}}
            """, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static InboxAnalyzerResponse Response() => new("""
        {"items":[{"kind":"Action","title":"Review","description":null,
        "properties":{"source":null,"waitingSince":null,"waitingSinceEvidence":null,"unresolvedDateText":null},
        "projectIds":[],"ambiguities":[]}]}
        """, "fake");

    private sealed class FakeAnalyzer(Func<InboxAnalysisRequest, CancellationToken, Task<InboxAnalyzerResponse>> run) : IInboxAnalyzer
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public InboxAnalyzerDescriptor Descriptor { get; init; } = new("fake", "v1", "v1");
        public Task<InboxAnalyzerResponse> AnalyzeAsync(InboxAnalysisRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return run(request, cancellationToken);
        }
    }

    private sealed class HostFactory(string connection, IInboxAnalyzer ai, bool enabled = true, bool key = true,
        bool background = true, int parallelism = 1)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Default", connection);
            builder.UseSetting("Telegram:BotToken", "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi");
            builder.UseSetting("Telegram:BotUsername", "OwnDayBot");
            builder.UseSetting("Telegram:WebhookSecret", "test-webhook-secret");
            builder.UseSetting("InboxAnalysis:OpenAI:Enabled", enabled.ToString());
            builder.UseSetting("InboxAnalysis:OpenAI:ApiKey", key ? "dummy-test-key" : "");
            builder.UseSetting("InboxAnalysis:Background:PollSeconds", "1");
            builder.UseSetting("InboxAnalysis:Background:Enabled", background.ToString());
            builder.UseSetting("InboxAnalysis:Background:Parallelism", parallelism.ToString());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddHostedService<InboxAnalysisWorker>();
                services.RemoveAll<IInboxAnalyzer>();
                services.AddSingleton(ai);
                services.RemoveAll<ITelegramMessageCleaner>();
                services.AddSingleton<ITelegramMessageCleaner, NoOpCleaner>();
            });
        }

        private sealed class NoOpCleaner : ITelegramMessageCleaner
        {
            public Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
