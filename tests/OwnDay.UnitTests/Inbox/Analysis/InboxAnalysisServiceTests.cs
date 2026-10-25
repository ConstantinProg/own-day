using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.UnitTests.Inbox.Analysis;

public sealed class InboxAnalysisServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
    private static readonly UserId Alice = new(11);
    private static readonly UserId Bob = new(12);

    [Fact]
    public async Task AnalyzeAsync_MultipleCanonicalProposals_SavesVersionsWithoutChangingDomainState()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = await fixture.CaptureAsync("купит молоко; ремонт; жду ответ; ссылка; может в горы");
        var ai = new FakeAnalyzer(async (request, token) =>
        {
            Assert.Equal(item.OriginalText, request.OriginalText);
            Assert.Equal(item.CapturedAt, request.CapturedAt);
            Assert.Null(request.Context.TimeZoneId);
            Assert.Equal(5, request.SupportedTypes.Count);
            Assert.Null(fixture.Db.Database.CurrentTransaction);
            // The pending attempt must already be committed and readable from another context.
            await using var reader = new OwnDayDbContext(fixture.Options);
            Assert.Equal(InboxAnalysisStatus.Pending, (await reader.InboxAnalyses.OrderByDescending(row => row.Version).FirstAsync(token)).Status);
            return Response(Enum.GetValues<InboxTargetKind>().Select(kind =>
                InboxAnalysisValidatorTests.Proposal(kind, kind == InboxTargetKind.Action ? "Купить молоко" : kind.ToString())).ToArray());
        });
        var service = fixture.Service(ai);

        var first = await service.AnalyzeAsync(Alice, item.Id, TestContextToken);
        var second = await service.AnalyzeAsync(Alice, item.Id, TestContextToken);
        fixture.Db.ChangeTracker.Clear();
        var history = await service.GetAsync(Alice, item.Id, TestContextToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.Equal(new[] { 2, 1 }, history.Select(row => row.Version));
        Assert.All(history, row =>
        {
            Assert.Equal(InboxAnalysisStatus.Completed, row.Status);
            Assert.Equal(5, row.Proposals.Count);
            Assert.Equal(Now, row.CreatedAt);
            Assert.Equal(Now, row.CompletedAt);
            Assert.Equal("fake-resolved-model", row.Model);
            Assert.Equal("inbox-v1", row.SchemaVersion);
            Assert.Equal("test-v1", row.PromptVersion);
            Assert.Equal("Купить молоко", row.Proposals.Single(proposal => proposal.Kind == InboxTargetKind.Action).ReadProposal().Title);
        });
        Assert.Equal(first.ContextFingerprint, second.ContextFingerprint);
        Assert.Equal(first.InputFingerprint, second.InputFingerprint);
        Assert.Equal(10, await fixture.Db.Set<InboxAnalysisProposal>().CountAsync());
        Assert.Empty(await service.GetAsync(Bob, item.Id, TestContextToken));
        var original = await fixture.Db.InboxItems.SingleAsync();
        Assert.Equal(item.OriginalText, original.OriginalText);
        Assert.Equal(InboxItemStatus.Active, original.Status);
        Assert.Null(original.TargetId);
        Assert.Empty(fixture.Db.Actions);
        Assert.Empty(fixture.Db.Projects);
        Assert.Empty(fixture.Db.WaitingFors);
        Assert.Empty(fixture.Db.References);
        Assert.Empty(fixture.Db.SomedayMaybes);
    }

    [Fact]
    public async Task AnalyzeAsync_ProjectContext_IsBoundedOrderedOwnedAndFingerprintChangesWithState()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = await fixture.CaptureAsync("Купить молоко");
        var completed = Project.Create(Alice, "Completed", Now);
        completed.Complete(Now);
        fixture.Db.Projects.Add(completed);
        fixture.Db.Projects.Add(Project.Create(Bob, "Private", Now));
        for (var i = 0; i < 52; i++)
        {
            fixture.Db.Projects.Add(Project.Create(Alice, "Project " + i, Now));
        }
        await fixture.Db.SaveChangesAsync();
        var ai = new FakeAnalyzer((request, _) =>
        {
            Assert.Equal(50, request.Context.Projects.Count);
            Assert.True(request.Context.ProjectsTruncated);
            Assert.Equal(request.Context.Projects.OrderBy(project => project.Id), request.Context.Projects);
            Assert.DoesNotContain(request.Context.Projects, project => project.Title is "Private" or "Completed");
            return Task.FromResult(Response(InboxAnalysisValidatorTests.Proposal(InboxTargetKind.Action, "Купить молоко")));
        });
        var service = fixture.Service(ai);
        var first = await service.AnalyzeAsync(Alice, item.Id, TestContextToken);
        var snapshot = JsonSerializer.Deserialize<InboxAnalysisContext>(first!.ContextJson, InboxAnalysisJson.Options)!;
        (await fixture.Db.Projects.SingleAsync(project => project.Id == snapshot.Projects[0].Id)).Complete(Now);
        await fixture.Db.SaveChangesAsync();
        var second = await service.AnalyzeAsync(Alice, item.Id, TestContextToken);
        Assert.NotEqual(first.ContextFingerprint, second!.ContextFingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalyzeAsync_UnknownOrForeignProject_StoresInvalidAttemptWithoutProposals(bool foreign)
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = await fixture.CaptureAsync("Купить молоко");
        var project = Project.Create(Bob, "Private", Now);
        fixture.Db.Projects.Add(project);
        await fixture.Db.SaveChangesAsync();
        var ai = new FakeAnalyzer((_, _) => Task.FromResult(Response(
            InboxAnalysisValidatorTests.Proposal(InboxTargetKind.Action, "Купить молоко") with
            { ProjectIds = [foreign ? project.Id : 9999] })));

        var result = await fixture.Service(ai).AnalyzeAsync(Alice, item.Id, TestContextToken);

        Assert.Equal(InboxAnalysisStatus.InvalidResponse, result!.Status);
        Assert.Empty(result.Proposals);
        Assert.Empty(fixture.Db.Set<InboxAnalysisProposal>());
        Assert.Contains("invalid_response", result.DiagnosticsJson);
    }

    [Fact]
    public async Task AnalyzeAsync_ForeignMissingAndTerminalInbox_DoesNotCallAnalyzer()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = await fixture.CaptureAsync("Private");
        var ai = new FakeAnalyzer((_, _) => throw new InvalidOperationException("Must not be called"));
        var service = fixture.Service(ai);
        Assert.Null(await service.AnalyzeAsync(Bob, item.Id, TestContextToken));
        Assert.Null(await service.AnalyzeAsync(Alice, 9999, TestContextToken));
        item.Discard(Now);
        await fixture.Db.SaveChangesAsync();
        Assert.Null(await service.AnalyzeAsync(Alice, item.Id, TestContextToken));
        Assert.Empty(fixture.Db.InboxAnalyses);
    }

    [Fact]
    public async Task AnalyzeAsync_ProviderFailure_StoresSafeDiagnosticsAndAllowsNewVersion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = await fixture.CaptureAsync("Купить молоко");
        var service = fixture.Service(new FakeAnalyzer((_, _) => throw new InboxAnalyzerException((InboxAnalysisCode)int.MaxValue)));
        var result = await service.AnalyzeAsync(Alice, item.Id, TestContextToken);
        Assert.Equal(InboxAnalysisStatus.Failed, result!.Status);
        Assert.DoesNotContain("sensitive", result.DiagnosticsJson);
        Assert.Equal("{\"code\":\"provider_failure\"}", result.DiagnosticsJson);
        var next = await fixture.Service(new FakeAnalyzer((_, _) => Task.FromResult(Response(
            InboxAnalysisValidatorTests.Proposal(InboxTargetKind.Action, "Купить молоко")))))
            .AnalyzeAsync(Alice, item.Id, TestContextToken);
        Assert.Equal(2, next!.Version);
        Assert.Equal(InboxAnalysisStatus.Completed, next.Status);
    }

    [Fact]
    public async Task AnalyzeAsync_CallerCancellation_PropagatesTokenAndLeavesCommittedPendingAttempt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = await fixture.CaptureAsync("Купить молоко");
        using var cancellation = new CancellationTokenSource();
        var ai = new FakeAnalyzer((_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service(ai).AnalyzeAsync(Alice, item.Id, cancellation.Token));
        Assert.Equal(InboxAnalysisStatus.Pending, (await fixture.Db.InboxAnalyses.SingleAsync()).Status);
        Assert.Empty(fixture.Db.Set<InboxAnalysisProposal>());
    }

    [Fact]
    public async Task AnalyzeAsync_ExistingTransaction_RejectsBeforeNetworkCall()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = await fixture.CaptureAsync("Купить молоко");
        await using var transaction = await fixture.Db.Database.BeginTransactionAsync();
        var ai = new FakeAnalyzer((_, _) => throw new InvalidOperationException("Must not be called"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service(ai).AnalyzeAsync(Alice, item.Id, TestContextToken));
        Assert.Contains("outside an existing transaction", error.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_InputAboveLimit_RejectsWithoutTruncatingOriginal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = await fixture.CaptureAsync(new string('x', InboxAnalysisService.MaxInputCharacters + 1));
        var ai = new FakeAnalyzer((_, _) => throw new InvalidOperationException("Must not be called"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service(ai).AnalyzeAsync(Alice, item.Id, TestContextToken));
        Assert.Empty(fixture.Db.InboxAnalyses);
        Assert.Equal(InboxAnalysisService.MaxInputCharacters + 1, item.OriginalText.Length);
    }

    [Theory]
    [InlineData("a\0b", "Action")]
    [InlineData("Reply", "Action, Project")]
    public async Task AnalyzeAsync_InvalidProposalTextOrKind_PersistsInvalidResponseWithoutProposals(string title, string kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = await fixture.CaptureAsync("Reply");
        var json = InboxAnalysisValidatorTests.Json(InboxAnalysisValidatorTests.Proposal(InboxTargetKind.Action, title))
            .Replace("\"kind\":\"Action\"", "\"kind\":\"" + kind + "\"", StringComparison.Ordinal);
        var ai = new FakeAnalyzer((_, _) => Task.FromResult(new InboxAnalyzerResponse(json, "fake-model")));
        var service = fixture.Service(ai);

        var result = await service.AnalyzeAsync(Alice, item.Id, TestContextToken);
        fixture.Db.ChangeTracker.Clear();
        var saved = Assert.Single(await service.GetAsync(Alice, item.Id, TestContextToken));

        Assert.NotNull(result);
        Assert.Equal(result.Id, saved.Id);
        Assert.Equal(InboxAnalysisStatus.InvalidResponse, saved.Status);
        Assert.NotNull(saved.CompletedAt);
        Assert.Empty(saved.Proposals);
        Assert.Empty(fixture.Db.Set<InboxAnalysisProposal>());
        Assert.Contains("invalid_response", saved.DiagnosticsJson);
    }

    [Theory]
    [InlineData("old-model", "inbox-v1", "test-v1", "descriptor_mismatch")]
    [InlineData("fake-model", "inbox-v0", "test-v1", "descriptor_mismatch")]
    [InlineData("fake-model", "inbox-v1", "test-v0", "descriptor_mismatch")]
    [InlineData("fake-model", "inbox-v1", "test-v1", "validated")]
    public async Task ProcessJobAsync_ResumedAnalysis_OnlyCallsProviderWithMatchingDescriptor(
        string model, string schema, string prompt, string expectedCode)
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = await fixture.CaptureAsync("Review");
        var context = JsonSerializer.Serialize(new InboxAnalysisContext(null, [], false), InboxAnalysisJson.Options);
        var analysis = InboxAnalysis.Start(Alice, item.Id, 1, new(model, schema, prompt), context, "hash", "hash", Now);
        var jobs = new RecordingJobStore(analysis);
        var calls = 0;
        var ai = new FakeAnalyzer((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(InboxAnalysisValidatorTests.Proposal(InboxTargetKind.Action, "Review")));
        });
        var lease = new InboxAnalysisLease(Guid.NewGuid(), Guid.NewGuid(), Alice, item.Id, 2);

        var code = await fixture.Service(ai).ProcessJobAsync(lease, jobs, new(), TestContextToken);

        Assert.Equal(expectedCode, code);
        Assert.Equal(expectedCode == "validated" ? 1 : 0, calls);
        Assert.Equal(expectedCode, jobs.Outcome!.Code.ToStorageValue());
        Assert.Null(jobs.RetryAfter);
        if (expectedCode == "descriptor_mismatch")
        {
            Assert.Null(jobs.Outcome.Output);
            Assert.Equal("{\"code\":\"descriptor_mismatch\"}", jobs.Outcome.DiagnosticsJson);
        }
        else
        {
            Assert.NotNull(jobs.Outcome.Output);
        }
    }

    [Theory]
    [InlineData(null, null, null, "descriptor_mismatch")]
    [InlineData("high", 8192, 60, "descriptor_mismatch")]
    [InlineData("low", 4096, 60, "descriptor_mismatch")]
    [InlineData("low", 8192, 30, "descriptor_mismatch")]
    [InlineData("low", 8192, 60, "validated")]
    public async Task ProcessJobAsync_ResumedParameters_OnlyCallsProviderWithExactSnapshot(
        string? effort, int? tokens, int? timeout, string expectedCode)
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = await fixture.CaptureAsync("Review");
        var context = JsonSerializer.Serialize(new InboxAnalysisContext(null, [], false), InboxAnalysisJson.Options);
        var analysis = InboxAnalysis.Start(Alice, item.Id, 1,
            new("fake-model", "inbox-v1", "test-v1", effort, tokens, timeout), context, "hash", "hash", Now);
        var jobs = new RecordingJobStore(analysis);
        var calls = 0;
        var ai = new FakeAnalyzer((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(InboxAnalysisValidatorTests.Proposal(InboxTargetKind.Action, "Review")));
        }) { Descriptor = new("fake-model", "inbox-v1", "test-v1", "low", 8192, 60) };

        var code = await fixture.Service(ai).ProcessJobAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), Alice, item.Id, 2), jobs, new(), TestContextToken);

        Assert.Equal(expectedCode, code);
        Assert.Equal(expectedCode == "validated" ? 1 : 0, calls);
        Assert.Null(jobs.RetryAfter);
        Assert.Equal(effort, analysis.ReasoningEffort);
        Assert.Equal(tokens, analysis.MaxOutputTokens);
        Assert.Equal(timeout, analysis.TimeoutSeconds);
    }

    private sealed class RecordingJobStore(InboxAnalysis analysis) : IInboxAnalysisJobStore
    {
        public InboxAnalysisOutcome? Outcome { get; private set; }
        public TimeSpan? RetryAfter { get; private set; }

        public Task<InboxAnalysis?> StartAsync(InboxAnalysisLease lease, InboxAnalyzerDescriptor descriptor,
            string contextJson, string contextFingerprint, string inputFingerprint, CancellationToken cancellationToken) =>
            Task.FromResult<InboxAnalysis?>(analysis);

        public Task<InboxAnalysisJobResult?> FinishAsync(InboxAnalysisLease lease, InboxAnalysisOutcome outcome,
            TimeSpan? retryAfter, CancellationToken cancellationToken)
        {
            Outcome = outcome;
            RetryAfter = retryAfter;
            return Task.FromResult<InboxAnalysisJobResult?>(new(
                outcome.Output is null ? InboxAnalysisJobStatus.Failed : InboxAnalysisJobStatus.Completed, outcome.Code));
        }

        public Task<InboxAnalysisLease?> ClaimAsync(TimeSpan lease, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecoverAsync(bool processingEnabled, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static CancellationToken TestContextToken => CancellationToken.None;

    private static InboxAnalyzerResponse Response(params InboxProposal[] proposals) =>
        new(InboxAnalysisValidatorTests.Json(proposals), "fake-resolved-model", "request-test", 100, 50);

    private sealed class FakeAnalyzer(Func<InboxAnalysisRequest, CancellationToken, Task<InboxAnalyzerResponse>> run) : IInboxAnalyzer
    {
        public InboxAnalyzerDescriptor Descriptor { get; init; } = new("fake-model", "inbox-v1", "test-v1");
        public Task<InboxAnalyzerResponse> AnalyzeAsync(InboxAnalysisRequest request, CancellationToken cancellationToken) =>
            run(request, cancellationToken);
    }

    private sealed class Fixture(SqliteConnection connection, DbContextOptions<OwnDayDbContext> options) : IAsyncDisposable
    {
        public DbContextOptions<OwnDayDbContext> Options { get; } = options;
        public OwnDayDbContext Db { get; } = new(options);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var fixture = new Fixture(connection, new DbContextOptionsBuilder<OwnDayDbContext>().UseSqlite(connection).Options);
            await fixture.Db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public InboxAnalysisService Service(IInboxAnalyzer ai) => new(new EfInboxItemStore(Db),
            new EfInboxAnalysisStore(Db, new EfInboxProcessingStore(Db)), ai, new(), new FixedTimeProvider(Now));

        public async Task<InboxItem> CaptureAsync(string text) =>
            await new EfInboxItemStore(Db).AddAsync(InboxItem.Capture(Alice, text, Now), TestContextToken);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
