using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OwnDay.InboxEvaluation;

public sealed record ReviewDecision(string Status, string Notes);
public sealed record CaseReview(string ScenarioId, string ScenarioHash, Dictionary<string, ReviewDecision> Dimensions);
public sealed record ManualReview(string RunId, string Reviewer, CaseReview[] Cases);
public sealed record Metric(int Passed, int Total)
{
    public double? Rate => Total == 0 ? null : (double)Passed / Total;
}

public static class EvaluationReports
{
    private static readonly IReadOnlyDictionary<string, Func<AutomaticChecks, bool>> Checks =
        new Dictionary<string, Func<AutomaticChecks, bool>>
    {
        ["structural"] = c => c.Structural, ["classification"] = c => c.Classification,
        ["splitting_count"] = c => c.Splitting, ["property_sets"] = c => c.Properties, ["project_id_sets"] = c => c.Links,
        ["ambiguity_presence"] = c => c.Ambiguities, ["lexical_content_guards"] = c => c.ContentAnchors, ["joint"] = c => c.Joint
    };

    public static async Task SaveAsync(string directory, RunManifest manifest, List<CaseResult> results, bool cancelled,
        CancellationToken cancellationToken)
    {
        object Metrics(IEnumerable<CaseResult> rows)
        {
            var cases = rows.ToArray();
            return Checks.ToDictionary(kv => kv.Key, kv => new Metric(cases.Count(r => kv.Value(r.Checks)), cases.Length));
        }

        var failures = results.Where(r => !r.Checks.Joint).Select(r => new { r.ScenarioId, r.Status, r.Checks });
        var summary = new
        {
            manifest.RunId, completed = results.Count, selected = manifest.Scenarios.Length, cancelled,
            complete = results.Count == manifest.Scenarios.Length,
            metrics = Metrics(results), byTag = manifest.Scenarios.SelectMany(s => s.Tags).Distinct().ToDictionary(
                tag => tag, tag => Metrics(results.Where(r => manifest.Scenarios.Single(s => s.Id == r.ScenarioId).Tags.Contains(tag)))),
            inputTokens = results.Sum(r => r.Response?.InputTokens ?? 0),
            outputTokens = results.Sum(r => r.Response?.OutputTokens ?? 0),
            cachedInputTokens = results.Sum(r => r.Response?.CachedInputTokens ?? 0),
            usageKnownCases = results.Count(r => r.Response?.InputTokens is not null && r.Response?.OutputTokens is not null),
            estimatedUsd = results.Count > 0 && results.All(r => r.EstimatedUsd is not null)
                ? results.Sum(r => r.EstimatedUsd) : null,
            knownEstimatedUsd = results.Sum(r => r.EstimatedUsd ?? 0),
            costKnownCases = results.Count(r => r.EstimatedUsd is not null),
            costNote = "Caller-supplied USD/1M base rates; excludes cache-write premiums, regional/service-tier surcharges and taxes. Unknown/failed call usage is not zero cost. No current pricing is assumed.",
            failures,
            interpretation = "Automatic diagnostics, not semantic accuracy. splitting_count checks count only; lexical guards do not prove canonicalization. Review all five dimensions manually."
        };
        await WriteAsync(Path.Combine(directory, "summary.json"), summary, cancellationToken);
        var review = new ManualReview(manifest.RunId, "", results.Select(r => new CaseReview(r.ScenarioId,
            r.ScenarioHash, EvaluationDimensions.All.ToDictionary(d => d, _ => new ReviewDecision("pending", "")))).ToArray());
        await WriteAsync(Path.Combine(directory, "manual-review.json"), review, cancellationToken);
        await WriteReadableReportAsync(directory, manifest, results, cancellationToken);
    }

    public static async Task RenderAsync(string directory, CancellationToken cancellationToken)
    {
        var (manifest, results) = await LoadAsync(directory, cancellationToken);
        await WriteReadableReportAsync(directory, manifest, results, cancellationToken);
    }

    private static async Task WriteReadableReportAsync(string directory, RunManifest manifest, List<CaseResult> results,
        CancellationToken cancellationToken)
    {
        var report = new StringBuilder();
        report.AppendLine($"# Inbox evaluation {manifest.RunId}");
        report.AppendLine();
        report.AppendLine($"Model: {manifest.Descriptor.Model}; prompt: {manifest.Descriptor.PromptVersion}; dataset: {manifest.DatasetVersion}.");
        report.AppendLine($"Completed {results.Count}/{manifest.Scenarios.Length} selected scenarios. Request cap: {manifest.MaxRequests}.");
        report.AppendLine();
        report.AppendLine("Automatic diagnostics only. Type-aligned property/link sets and count checks do not establish semantic correctness. Canonicalization requires review; lexical guards are not an equivalence test.");
        report.AppendLine();
        report.AppendLine("| Check | Passed / completed | Rate |");
        report.AppendLine("| --- | --- | --- |");
        foreach (var (name, check) in Checks)
        {
            var passed = results.Count(r => check(r.Checks));
            var rate = results.Count == 0 ? "n/a" : ((double)passed / results.Count).ToString("P1", CultureInfo.InvariantCulture);
            report.AppendLine($"| {name} | {passed}/{results.Count} | {rate} |");
        }

        report.AppendLine();
        report.AppendLine("Review all five dimensions using review-packet.json and manual-review.json, then run the review command. Pending/uncertain judgments must not count as passes. AI-assisted judgments must identify the reviewer and are not objective truth.");
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.md"), report.ToString(), cancellationToken);
        // Structured packet keeps adversarial strings as data and presents source, expectations and outputs together.
        var packet = new
        {
            manifest.RunId,
            instructions = "For each dimension choose pass/fail/uncertain against reviewCriteria and acceptable variants; record reasons for fail/uncertain, reviewer identity, and whether review was human or AI-assisted. Check every result, not only automatic failures. No LLM judge is called.",
            cases = results.Select(r => new
            {
                scenario = manifest.Scenarios.Single(s => s.Id == r.ScenarioId),
                r.Status, r.Checks, output = r.Response?.Json
            })
        };
        await WriteAsync(Path.Combine(directory, "review-packet.json"), packet, cancellationToken);
    }

    public static async Task<(RunManifest Manifest, List<CaseResult> Results)> LoadAsync(string directory, CancellationToken cancellationToken)
    {
        var manifest = JsonSerializer.Deserialize<RunManifest>(await File.ReadAllTextAsync(Path.Combine(directory, "run.json"), cancellationToken), EvaluationJson.Options)
            ?? throw new InvalidDataException("Missing run manifest.");
        var results = new List<CaseResult>();
        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(directory, "results.jsonl"), cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                results.Add(JsonSerializer.Deserialize<CaseResult>(line, EvaluationJson.Options)
                    ?? throw new InvalidDataException("Invalid result line."));
            }
        }

        if (results.Select(r => r.ScenarioId).Distinct().Count() != results.Count ||
            results.Any(r => !manifest.Scenarios.Any(s => s.Id == r.ScenarioId && EvaluationRun.Hash(s) == r.ScenarioHash)))
        {
            throw new InvalidDataException("Results do not match the saved scenarios.");
        }

        return (manifest, results);
    }

    public static async Task ReviewAsync(string directory, CancellationToken cancellationToken)
    {
        var (manifest, results) = await LoadAsync(directory, cancellationToken);
        var review = JsonSerializer.Deserialize<ManualReview>(await File.ReadAllTextAsync(Path.Combine(directory, "manual-review.json"), cancellationToken), EvaluationJson.Options)
            ?? throw new InvalidDataException("Missing manual review.");
        if (review.RunId != manifest.RunId || review.Cases.Length != results.Count ||
            review.Cases.Select(r => r.ScenarioId).Distinct().Count() != results.Count ||
            review.Cases.Any(r => !results.Any(a => a.ScenarioId == r.ScenarioId && a.ScenarioHash == r.ScenarioHash) ||
                r.Dimensions.Count != EvaluationDimensions.All.Length || EvaluationDimensions.All.Any(d =>
                    !r.Dimensions.TryGetValue(d, out var decision) || decision.Status is not ("pending" or "pass" or "fail" or "uncertain") ||
                    (decision.Status is "fail" or "uncertain" && string.IsNullOrWhiteSpace(decision.Notes)))) ||
            (review.Cases.Any(r => r.Dimensions.Values.Any(d => d.Status != "pending")) && string.IsNullOrWhiteSpace(review.Reviewer)))
        {
            throw new InvalidDataException("Review must identify its reviewer and match this run; failures/uncertainty require notes.");
        }

        var metrics = EvaluationDimensions.All.ToDictionary(d => d, d => new
        {
            passed = review.Cases.Count(c => c.Dimensions[d].Status == "pass"),
            failed = review.Cases.Count(c => c.Dimensions[d].Status == "fail"),
            uncertain = review.Cases.Count(c => c.Dimensions[d].Status == "uncertain"),
            pending = review.Cases.Count(c => c.Dimensions[d].Status == "pending"),
            total = review.Cases.Length
        });
        await WriteAsync(Path.Combine(directory, "manual-summary.json"), new { review.RunId, review.Reviewer, metrics,
            note = "Reviewer judgments, not objective ground truth. Pending/uncertain judgments are not passes. See run.json scenario reviewCriteria and original outputs." }, cancellationToken);
    }

    public static async Task CompareAsync(string baseline, string candidate, string outputPath, CancellationToken cancellationToken)
    {
        var (beforeManifest, before) = await LoadAsync(baseline, cancellationToken);
        var (afterManifest, after) = await LoadAsync(candidate, cancellationToken);
        if (beforeManifest.ScorerVersion != afterManifest.ScorerVersion)
        {
            throw new InvalidDataException("Scorer versions must match for automatic comparisons.");
        }

        var pairs = before.Join(after, b => (b.ScenarioId, b.ScenarioHash), a => (a.ScenarioId, a.ScenarioHash), (b, a) => new
        {
            b.ScenarioId, before = b.Checks, after = a.Checks,
            regression = b.Checks.Joint && !a.Checks.Joint, improvement = !b.Checks.Joint && a.Checks.Joint,
            inputTokenDelta = a.Response?.InputTokens - b.Response?.InputTokens,
            outputTokenDelta = a.Response?.OutputTokens - b.Response?.OutputTokens
        }).ToArray();
        if (pairs.Length == 0)
        {
            throw new InvalidDataException("No identical scenarios to compare.");
        }

        // The comparison must not overwrite any artifact from either experiment.
        await using var stream = new FileStream(outputPath, FileMode.CreateNew);
        await JsonSerializer.SerializeAsync(stream, new
        {
            baseline = new { beforeManifest.RunId, beforeManifest.Descriptor, beforeManifest.DatasetHash, beforeManifest.ScorerVersion },
            candidate = new { afterManifest.RunId, afterManifest.Descriptor, afterManifest.DatasetHash, afterManifest.ScorerVersion },
            paired = pairs.Length, unpairedBefore = before.Count - pairs.Length, unpairedAfter = after.Count - pairs.Length,
            regressions = pairs.Count(p => p.regression), improvements = pairs.Count(p => p.improvement), pairs,
            note = "Only identical scenario hashes are paired; inspect outputs and manual reviews for semantic changes. Aliases and stochastic sampling prevent bit-for-bit reproducibility."
        }, EvaluationJson.Options, cancellationToken);
    }

    public static Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, EvaluationJson.Options), cancellationToken);
}
