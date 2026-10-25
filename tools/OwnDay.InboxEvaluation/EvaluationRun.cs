using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OwnDay.Application.Inbox.Analysis;

namespace OwnDay.InboxEvaluation;

public sealed record TokenPrices(decimal Input, decimal CachedInput, decimal Output)
{
    public decimal? Estimate(InboxAnalyzerResponse response) =>
        response.InputTokens is { } input && response.OutputTokens is { } output && response.CachedInputTokens is { } cached
            ? ((input - cached) * Input + cached * CachedInput + output * Output) / 1_000_000m : null;
}

public sealed record RunManifest(string RunId, DateTime StartedAt, string DatasetVersion, string DatasetHash,
    GoldenScenario[] Scenarios, InboxAnalyzerDescriptor Descriptor, string Prompt, string Schema,
    string PromptHash, string SchemaHash, string Revision, string Runtime, string SdkVersion, int MaxRequests,
    TokenPrices? Prices, string ScorerVersion = "inbox-scorer-v1");

public sealed record CaseResult(string ScenarioId, string ScenarioHash, string Status, double DurationMilliseconds,
    InboxAnalyzerResponse? Response, AutomaticChecks Checks, decimal? EstimatedUsd);

public sealed class EvaluationRun(IInboxAnalyzer analyzer)
{
    public async Task<bool> RunAsync(GoldenDataset dataset, GoldenScenario[] scenarios, string outputDirectory,
        int maxRequests, TokenPrices? prices, string revision, CancellationToken cancellationToken)
    {
        if (maxRequests <= 0 || scenarios.Length == 0 || scenarios.Length > maxRequests ||
            scenarios.Select(s => s.Id).Distinct().Count() != scenarios.Length)
        {
            throw new ArgumentException("Select between one and maxRequests unique scenarios.");
        }

        dataset.Validate();
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new IOException("The experiment directory must be new or empty.");
        }

        var prompt = Infrastructure.OpenAIAnalysis.OpenAIInboxAnalysisFormat.GetPrompt(analyzer.Descriptor.PromptVersion);
        var schema = Infrastructure.OpenAIAnalysis.OpenAIInboxAnalysisFormat.Schema;
        var manifest = new RunManifest(Guid.NewGuid().ToString("N"), DateTime.UtcNow, dataset.Version, Hash(dataset),
            scenarios, analyzer.Descriptor, prompt, schema, Hash(prompt), Hash(schema), revision,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            typeof(OpenAI.Chat.ChatClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            maxRequests, prices, GoldenScorer.Version);
        Directory.CreateDirectory(outputDirectory);
        // Never overwrite a previous experiment, even if its original process was interrupted.
        await using (var stream = new FileStream(Path.Combine(outputDirectory, "run.json"), FileMode.CreateNew))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, EvaluationJson.Options, cancellationToken);
        }

        await using var results = new StreamWriter(new FileStream(Path.Combine(outputDirectory, "results.jsonl"), FileMode.CreateNew), Encoding.UTF8);
        var completed = new List<CaseResult>();
        var consecutiveFailures = 0;
        foreach (var scenario in scenarios)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var timer = Stopwatch.StartNew();
            InboxAnalyzerResponse? response = null;
            var status = "validated";
            InboxAnalysisOutput? output = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(analyzer.Descriptor.TimeoutSeconds ?? 60));
            try
            {
                response = await analyzer.AnalyzeAsync(scenario.Request, timeout.Token).WaitAsync(timeout.Token);
                output = new InboxAnalysisValidator().ParseAndValidate(response.Json, scenario.Request);
                if (output is null)
                {
                    status = "invalid_response";
                }
            }
            catch (InboxAnalyzerException exception)
            {
                status = exception.Code.ToStorageValue();
            }
            catch (OperationCanceledException)
            {
                status = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            }
            catch (Exception)
            {
                // Preserve partial artifacts without disclosing SDK exception messages or secrets.
                status = "unexpected_failure";
            }

            var result = new CaseResult(scenario.Id, Hash(scenario), status, timer.Elapsed.TotalMilliseconds,
                response, GoldenScorer.Score(scenario, output), response is null ? null : prices?.Estimate(response));
            completed.Add(result);
            // Bounded local checkpoints deliberately outlive caller cancellation to retain already billed responses.
            // This cleanup never starts another provider call after cancellation.
            using var checkpoint = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await results.WriteLineAsync(JsonSerializer.Serialize(result, EvaluationJson.Lines).AsMemory(), checkpoint.Token);
            await results.FlushAsync(checkpoint.Token);
            Console.WriteLine($"{completed.Count}/{scenarios.Length} {scenario.Id}: {status}; joint={result.Checks.Joint}");
            consecutiveFailures = status is "provider_failure" or "timeout" ? consecutiveFailures + 1 : 0;
            if (consecutiveFailures >= 3 || status is "configuration" or "budget_limit" or "rate_limit" or "permanent_failure" or "cancelled" or "unexpected_failure")
            {
                break;
            }
        }

        using var reportCheckpoint = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await EvaluationReports.SaveAsync(outputDirectory, manifest, completed, cancellationToken.IsCancellationRequested,
            reportCheckpoint.Token);
        return completed.Count == scenarios.Length && completed.All(r => r.Status == "validated");
    }

    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, EvaluationJson.Lines))));
}
