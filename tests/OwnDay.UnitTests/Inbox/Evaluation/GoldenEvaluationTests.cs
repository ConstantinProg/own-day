using System.Text.Json;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Domain.Inbox;
using OwnDay.InboxEvaluation;
using Xunit;

namespace OwnDay.UnitTests.Inbox.Evaluation;

public sealed class GoldenEvaluationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ownday-evaluation-" + Guid.NewGuid().ToString("N"));
    private static readonly InboxProposalProperties Empty = new()
    {
        Source = null, WaitingSince = null, WaitingSinceEvidence = null, UnresolvedDateText = null
    };

    [Fact]
    public async Task LoadAsync_GoldenDataset_CoversAllKindsAndRequiredRisksWithValidExpectations()
    {
        var dataset = await LoadAsync();
        Assert.True(dataset.Scenarios.Length >= 100);
        Assert.Equal(Enum.GetValues<InboxTargetKind>().Order(), dataset.Scenarios.SelectMany(s => s.Variants)
            .SelectMany(v => v.Items).SelectMany(i => i.Kinds).Distinct().Order());
        string[] tags = ["splitting", "canonicalization", "negation", "hypothetical", "ambiguity", "dates", "waiting", "links", "contradiction", "injection", "missing-context", "truncated", "typo"];
        Assert.All(tags, tag => Assert.Contains(dataset.Scenarios, s => s.Tags.Contains(tag)));
        Assert.Equal(dataset.Scenarios.Length, dataset.Scenarios.Select(s => EvaluationRun.Hash(s.Request)).Distinct().Count());
        Assert.All(dataset.Scenarios, s => Assert.All(EvaluationDimensions.All, d => Assert.False(string.IsNullOrWhiteSpace(s.ReviewCriteria[d]))));
    }

    [Fact]
    public async Task LoadAsync_PreviousDataset_RemainsValidAndVersioned()
    {
        var previous = await GoldenDataset.LoadAsync(Path.Combine(AppContext.BaseDirectory, "GoldenDataset", "golden-v1.json"), CancellationToken.None);
        Assert.Equal("inbox-golden-v1", previous.Version);
        Assert.Equal("AEEE045F79AF58C487E17240FF070AEDD89C00B69C198928394F1E09E6D02053",
            EvaluationRun.Hash(Infrastructure.OpenAIAnalysis.OpenAIInboxAnalysisFormat.GetPrompt("inbox-analysis-v1")));
    }

    [Fact]
    public async Task Validate_DuplicateIdsOrImpossibleEvidence_RejectsBeforeProviderCall()
    {
        var dataset = await LoadAsync();
        var scenario = dataset.Scenarios[0];
        Assert.Throws<InvalidDataException>(() => (dataset with { Scenarios = [scenario, scenario] }).Validate());
        var bad = scenario.Variants[0].Items[0] with { PropertySets = [Empty with { Source = "Invented person" }] };
        Assert.Throws<InvalidDataException>(() => (dataset with
        {
            Scenarios = [scenario with { Variants = [new("Invalid", [bad])] }]
        }).Validate());
    }

    [Fact]
    public async Task Score_ReorderedIndependentIntentions_MatchesWithoutLiteralTitles()
    {
        var scenario = (await LoadAsync()).Scenarios.Single(s => s.Id == "split-01");
        var output = Output(Proposal(InboxTargetKind.Action, "Отправка отчёта бухгалтеру"), Proposal(InboxTargetKind.Action, "Созвониться с мамой"));
        Assert.True(GoldenScorer.Score(scenario, output).Joint);
        Assert.False(GoldenScorer.Score(scenario, Output(output.Items[0], output.Items[0])).ContentAnchors);
    }

    [Fact]
    public async Task Score_AllowedAlternativeInterpretation_AcceptsMergedSupportingInformation()
    {
        var scenario = (await LoadAsync()).Scenarios.Single(s => s.Id == "split-03");
        Assert.True(GoldenScorer.Score(scenario, Output(Proposal(InboxTargetKind.Action, "Позвонить в клинику: Лесная, 7"))).Joint);
    }

    [Fact]
    public async Task Score_TentativeIntentAsCommitmentOrInventedProperties_FailsDiagnostics()
    {
        var dataset = await LoadAsync();
        var scenario = dataset.Scenarios.Single(s => s.Id == "someday-02");
        Assert.False(GoldenScorer.Score(scenario, Output(Proposal(InboxTargetKind.Action, "Купить 3D-принтер за деньги"))).Classification);
        var waiting = dataset.Scenarios.Single(s => s.Id == "waiting-03");
        Assert.False(GoldenScorer.Score(waiting, Output(Proposal(InboxTargetKind.WaitingFor, "Подтверждение бронирования") with
        {
            Properties = Empty with { Source = "Неизвестный отель" }
        })).Properties);
    }

    [Fact]
    public async Task Score_ProjectAlternativesAndDateEvidence_RequiresExpectedUncertaintyAndRoles()
    {
        var dataset = await LoadAsync();
        var scenario = dataset.Scenarios.Single(s => s.Id == "link-06");
        var proposal = Proposal(InboxTargetKind.Action, "Согласовать макет") with { ProjectIds = [201], Ambiguities = ["Неясный проект"] };
        Assert.False(GoldenScorer.Score(scenario, Output(proposal)).Links);
        Assert.True(GoldenScorer.Score(scenario, Output(proposal with { ProjectIds = [202, 201] })).Joint);
        Assert.False(GoldenScorer.Score(scenario, Output(proposal with { ProjectIds = [], Ambiguities = [] })).Ambiguities);
        var date = dataset.Scenarios.Single(s => s.Id == "date-12");
        Assert.False(GoldenScorer.Score(date, Output(Proposal(InboxTargetKind.WaitingFor, "Ответ Анны") with
        {
            Properties = Empty with { Source = "Анны", WaitingSince = DateTimeOffset.Parse("2026-10-20T09:00:00Z"), WaitingSinceEvidence = "2026-10-20T09:00:00Z" }
        })).Properties);
    }

    [Fact]
    public void Estimate_CachedUsageOrMissingUsage_DoesNotInventCost()
    {
        var prices = new TokenPrices(2m, 0.5m, 8m);
        Assert.Equal(0.0029m, prices.Estimate(new("{}", "fake", InputTokens: 1000, OutputTokens: 300, CachedInputTokens: 1000)));
        Assert.Null(prices.Estimate(new("{}", "fake", InputTokens: 1000, OutputTokens: 300)));
    }

    [Fact]
    public async Task Score_IncompatiblePiecesOfDifferentVariants_DoesNotReportJointSuccess()
    {
        var scenario = (await LoadAsync()).Scenarios.Single(s => s.Id == "waiting-01");
        var expected = scenario.Variants[0].Items[0];
        scenario = scenario with
        {
            Text = "Жду договор от Анны или Олега по одному из проектов.",
            Context = new(null, [new(101, "Первый проект", scenario.CapturedAt), new(102, "Второй проект", scenario.CapturedAt)], false),
            Variants =
            [
                new("First alternative", [expected with { PropertySets = [Empty with { Source = "Анны" }], ProjectIdSets = [[101]] }]),
                new("Second alternative", [expected with { PropertySets = [Empty with { Source = "Олега" }], ProjectIdSets = [[102]] }])
            ]
        };
        var mixed = Proposal(InboxTargetKind.WaitingFor, "Договор от Анны") with
        {
            Properties = Empty with { Source = "Анны" }, ProjectIds = [102]
        };
        var score = GoldenScorer.Score(scenario, Output(mixed));
        Assert.True(score.Properties);
        Assert.True(score.Links);
        Assert.False(score.Joint);
    }

    [Fact]
    public async Task Main_MissingExplicitRequestBudgetOrUnknownOption_RejectsBeforeReadingCredentials()
    {
        Assert.Equal(2, await OwnDay.InboxEvaluation.Program.Main(["run", "--settings-file", "does-not-exist"]));
        Assert.Equal(2, await OwnDay.InboxEvaluation.Program.Main(["run", "--max-request", "5"]));
    }

    [Fact]
    public async Task RunAsync_RequestBudgetAndExistingOutput_RejectsWithoutCallsOrOverwrites()
    {
        var dataset = await LoadAsync();
        var fake = new FakeAnalyzer(_ => Task.FromResult(Response(Proposal(InboxTargetKind.Action, "Купить молоко 2 литра"))));
        var run = new EvaluationRun(fake);
        await Assert.ThrowsAsync<ArgumentException>(() => run.RunAsync(dataset, dataset.Scenarios[..2], _directory, 1, null, "test", CancellationToken.None));
        Assert.Equal(0, fake.Calls);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "keep.txt"), "existing data");
        await Assert.ThrowsAsync<IOException>(() => run.RunAsync(dataset, dataset.Scenarios[..1], _directory, 1, null, "test", CancellationToken.None));
        Assert.Equal("existing data", await File.ReadAllTextAsync(Path.Combine(_directory, "keep.txt")));
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task RunAsync_ProviderConfigurationFailure_SavesPartialArtifactsAndStops()
    {
        var dataset = await LoadAsync();
        var fake = new FakeAnalyzer(_ => throw new InboxAnalyzerException(InboxAnalysisCode.Configuration));
        Assert.False(await new EvaluationRun(fake).RunAsync(dataset, dataset.Scenarios[..3], _directory, 3, null, "test", CancellationToken.None));
        Assert.Equal(1, fake.Calls);
        var (manifest, results) = await EvaluationReports.LoadAsync(_directory, CancellationToken.None);
        Assert.Equal(3, manifest.Scenarios.Length);
        Assert.Equal("configuration", Assert.Single(results).Status);
        Assert.Null(results[0].Response);
        Assert.False(results[0].Checks.Structural);
        Assert.True(File.Exists(Path.Combine(_directory, "summary.json")));
        Assert.True(File.Exists(Path.Combine(_directory, "manual-review.json")));
        Assert.True(File.Exists(Path.Combine(_directory, "review-packet.json")));
        Assert.Contains("Canonicalization requires review", await File.ReadAllTextAsync(Path.Combine(_directory, "summary.md")));
    }

    [Fact]
    public async Task RunAsync_TransportFailures_StopsAfterThreeWithoutRetryingScenarios()
    {
        var dataset = await LoadAsync();
        var fake = new FakeAnalyzer(_ => throw new InboxAnalyzerException(InboxAnalysisCode.ProviderFailure));
        Assert.False(await new EvaluationRun(fake).RunAsync(dataset, dataset.Scenarios[..5], _directory, 5, null, "test", CancellationToken.None));
        Assert.Equal(3, fake.Calls);
        Assert.Equal(3, (await EvaluationReports.LoadAsync(_directory, CancellationToken.None)).Results.Count);
    }

    [Fact]
    public async Task RunAsync_Cancellation_PropagatesTokenAndPreservesPartialReports()
    {
        var dataset = await LoadAsync();
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeAnalyzer(token =>
        {
            Assert.True(token.CanBeCanceled);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        });
        Assert.False(await new EvaluationRun(fake).RunAsync(dataset, dataset.Scenarios[..3], _directory, 3, null, "test", cancellation.Token));
        Assert.Equal(1, fake.Calls);
        Assert.Equal("cancelled", Assert.Single((await EvaluationReports.LoadAsync(_directory, CancellationToken.None)).Results).Status);
    }

    [Fact]
    public async Task ReviewAsync_PendingAndUncertainJudgments_AreNotCountedAsPasses()
    {
        var dataset = await LoadAsync();
        var fake = new FakeAnalyzer(_ => Task.FromResult(Response(Proposal(InboxTargetKind.Action, "Купить молоко 2 литра"))));
        Assert.True(await new EvaluationRun(fake).RunAsync(dataset, dataset.Scenarios[..1], _directory, 1, null, "test", CancellationToken.None));
        var path = Path.Combine(_directory, "manual-review.json");
        var review = JsonSerializer.Deserialize<ManualReview>(await File.ReadAllTextAsync(path), EvaluationJson.Options)!;
        review.Cases[0].Dimensions["canonicalization"] = new("uncertain", "Недостаточно информации для оценки");
        await EvaluationReports.WriteAsync(path, review);
        await Assert.ThrowsAsync<InvalidDataException>(() => EvaluationReports.ReviewAsync(_directory, CancellationToken.None));
        await EvaluationReports.WriteAsync(path, review with { Reviewer = "Test reviewer" });
        await EvaluationReports.ReviewAsync(_directory, CancellationToken.None);
        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "manual-summary.json")));
        var canonical = summary.RootElement.GetProperty("metrics").GetProperty("canonicalization");
        Assert.Equal(0, canonical.GetProperty("passed").GetInt32());
        Assert.Equal(1, canonical.GetProperty("uncertain").GetInt32());
    }

    [Fact]
    public async Task CompareAsync_ChangedScenarioHash_ExcludesUnpairedCase()
    {
        var dataset = await LoadAsync();
        var fake = new FakeAnalyzer(_ => Task.FromResult(Response(Proposal(InboxTargetKind.Action, "Купить молоко 2 литра"))));
        var before = Path.Combine(_directory, "before");
        var after = Path.Combine(_directory, "after");
        var run = new EvaluationRun(fake);
        await run.RunAsync(dataset, dataset.Scenarios[..2], before, 2, null, "before", CancellationToken.None);
        var changed = dataset.Scenarios[..2];
        changed[1] = changed[1] with { Text = changed[1].Text + " Пожалуйста." };
        await run.RunAsync(dataset with { Scenarios = changed }, changed, after, 2, null, "after", CancellationToken.None);
        var comparison = Path.Combine(_directory, "comparison.json");
        await EvaluationReports.CompareAsync(before, after, comparison, CancellationToken.None);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(comparison));
        Assert.Equal(1, json.RootElement.GetProperty("paired").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("unpairedAfter").GetInt32());
        await Assert.ThrowsAsync<IOException>(() => EvaluationReports.CompareAsync(before, after, comparison, CancellationToken.None));
        var manifestPath = Path.Combine(after, "run.json");
        var manifest = (await EvaluationReports.LoadAsync(after, CancellationToken.None)).Manifest;
        await EvaluationReports.WriteAsync(manifestPath, manifest with { ScorerVersion = "different-scorer" });
        await Assert.ThrowsAsync<InvalidDataException>(() => EvaluationReports.CompareAsync(before, after,
            Path.Combine(_directory, "different-scorer.json"), CancellationToken.None));
    }

    private static Task<GoldenDataset> LoadAsync() => GoldenDataset.LoadAsync(Path.Combine(AppContext.BaseDirectory, "GoldenDataset", "golden-v2.json"), CancellationToken.None);
    private static InboxProposal Proposal(InboxTargetKind kind, string title) => new()
    {
        Kind = kind, Title = title, Description = null, Properties = Empty, ProjectIds = [], Ambiguities = []
    };
    private static InboxAnalysisOutput Output(params InboxProposal[] items) => new() { Items = items };
    private static InboxAnalyzerResponse Response(params InboxProposal[] items) => new(JsonSerializer.Serialize(Output(items), InboxAnalysisJson.Options), "fake-model");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeAnalyzer(Func<CancellationToken, Task<InboxAnalyzerResponse>> call) : IInboxAnalyzer
    {
        public int Calls { get; private set; }
        public InboxAnalyzerDescriptor Descriptor { get; } = new("fake-model", "inbox-v1", "inbox-analysis-v2", TimeoutSeconds: 1);
        public Task<InboxAnalyzerResponse> AnalyzeAsync(InboxAnalysisRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return call(cancellationToken);
        }
    }
}
