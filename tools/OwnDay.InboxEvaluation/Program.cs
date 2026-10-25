using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OwnDay.Infrastructure.OpenAIAnalysis;

namespace OwnDay.InboxEvaluation;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "help")
            {
                Console.WriteLine("Commands: validate --dataset PATH; run --max-requests N [--dataset PATH --model MODEL --prompt-version VERSION --settings-file LOCAL_PATH --output DIRECTORY --ids id1,id2 --tag TAG --offset N --revision LABEL --max-output-tokens N --timeout-seconds N --reasoning-effort VALUE --input-usd-per-million N --cached-input-usd-per-million N --output-usd-per-million N]; compare --baseline DIR --candidate DIR --output FILE; render --run DIR; review --run DIR. API key: OPENAI_API_KEY or explicit --settings-file (never printed). No API calls for validate/compare/render/review.");
                return 0;
            }

            var options = Parse(args.Skip(1).ToArray());
            string Get(string name, string fallback) => options.GetValueOrDefault(name, fallback);
            string Required(string name) => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ArgumentException("Missing required option.");
            int Number(string name, int fallback, int min, int max) =>
                int.TryParse(Get(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
                    ? value : throw new ArgumentException("Invalid numeric limit.");

            switch (args[0])
            {
                case "validate":
                    Allow(options, "dataset");
                    var validation = await GoldenDataset.LoadAsync(Get("dataset", "evaluation/inbox/golden-v2.json"), cancellation.Token);
                    Console.WriteLine($"Valid dataset {validation.Version}: {validation.Scenarios.Length} scenarios.");
                    return 0;
                case "compare":
                    Allow(options, "baseline", "candidate", "output");
                    await EvaluationReports.CompareAsync(Required("baseline"), Required("candidate"), Required("output"), cancellation.Token);
                    return 0;
                case "review":
                    Allow(options, "run");
                    await EvaluationReports.ReviewAsync(Required("run"), cancellation.Token);
                    return 0;
                case "render":
                    Allow(options, "run");
                    await EvaluationReports.RenderAsync(Required("run"), cancellation.Token);
                    return 0;
                case "run":
                    Allow(options, "dataset", "max-requests", "model", "prompt-version", "settings-file", "output", "ids", "tag",
                        "offset", "revision", "max-output-tokens", "timeout-seconds", "reasoning-effort",
                        "input-usd-per-million", "cached-input-usd-per-million", "output-usd-per-million");
                    break;
                default:
                    throw new ArgumentException("Unknown command.");
            }

            _ = Required("max-requests"); // Paid work always needs an explicit finite request budget.
            var maxRequests = Number("max-requests", 0, 1, 10_000);
            var dataset = await GoldenDataset.LoadAsync(Get("dataset", "evaluation/inbox/golden-v2.json"), cancellation.Token);
            var requestedIds = options.TryGetValue("ids", out var ids) ? ids.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal) : null;
            if (requestedIds is not null && (requestedIds.Count == 0 || requestedIds.Any(id => !dataset.Scenarios.Any(s => s.Id == id))))
            {
                throw new ArgumentException("Unknown/empty scenario selection.");
            }

            var selected = dataset.Scenarios.Where(s => requestedIds is null || requestedIds.Contains(s.Id))
                .Where(s => !options.TryGetValue("tag", out var tag) || s.Tags.Contains(tag))
                .Skip(Number("offset", 0, 0, 100_000)).Take(maxRequests).ToArray();
            if (selected.Length == 0)
            {
                throw new ArgumentException("Empty scenario selection.");
            }

            var settings = new OpenAIInboxAnalysisOptions
            {
                Model = Get("model", new OpenAIInboxAnalysisOptions().Model),
                ReasoningEffort = Get("reasoning-effort", "low"),
                MaxOutputTokens = Number("max-output-tokens", 8192, 256, 16_384),
                TimeoutSeconds = Number("timeout-seconds", 60, 1, 300)
            };
            if (string.IsNullOrWhiteSpace(settings.Model) || settings.Model.Length > 200 ||
                settings.ReasoningEffort is not ("none" or "low" or "medium" or "high" or "xhigh" or "max"))
            {
                throw new ArgumentException("Invalid model settings.");
            }

            var promptVersion = Get("prompt-version", OpenAIInboxAnalysisFormat.PromptVersion);
            _ = OpenAIInboxAnalysisFormat.GetPrompt(promptVersion);
            var prices = ReadPrices(options);
            var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (options.TryGetValue("settings-file", out var settingsPath))
            {
                // Only extract credentials; never serialize/log local settings or accept an endpoint override.
                var local = await File.ReadAllTextAsync(settingsPath, cancellation.Token);
                var matches = Regex.Matches(local, @"\bsk-[A-Za-z0-9_-]{16,}");
                if (matches.Count != 1)
                {
                    throw new ArgumentException("Local settings must contain exactly one OpenAI API key.");
                }

                key = matches[0].Value;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Set OPENAI_API_KEY or explicitly supply --settings-file.");
            }

            var client = new ChatClient(settings.Model, new ApiKeyCredential(key), new OpenAIClientOptions
            {
                NetworkTimeout = TimeSpan.FromSeconds(settings.TimeoutSeconds), RetryPolicy = new ClientRetryPolicy(0)
            });
            var analyzer = new OpenAIInboxAnalyzer(client, Options.Create(settings), promptVersion);
            var directory = Get("output", Path.Combine("evaluation-results", DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]));
            var completed = await new EvaluationRun(analyzer).RunAsync(dataset, selected, directory, maxRequests, prices,
                Get("revision", "unspecified"), cancellation.Token);
            return cancellation.IsCancellationRequested ? 130 : completed ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Evaluation cancelled.");
            return 130;
        }
        catch (System.Text.Json.JsonException exception)
        {
            Console.Error.WriteLine($"Invalid JSON near line {exception.LineNumber}, byte {exception.BytePositionInLine}.");
            return 2;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Invalid config/dataset exceptions can embed secret values. Report types only.
            Console.Error.WriteLine($"Evaluation failed ({exception.GetType().Name}). Check options, dataset and output directory; use help.");
            return 2;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length % 2 != 0)
        {
            throw new ArgumentException("Options require values.");
        }

        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[i][2..], args[i + 1]))
            {
                throw new ArgumentException("Invalid or duplicate option.");
            }
        }

        return values;
    }

    private static void Allow(Dictionary<string, string> options, params string[] allowed)
    {
        if (options.Keys.Except(allowed, StringComparer.Ordinal).Any())
        {
            throw new ArgumentException("Unknown option.");
        }
    }

    private static TokenPrices? ReadPrices(Dictionary<string, string> options)
    {
        string[] names = ["input-usd-per-million", "cached-input-usd-per-million", "output-usd-per-million"];
        if (!names.Any(options.ContainsKey))
        {
            return null;
        }

        var prices = names.Select(name => options.TryGetValue(name, out var text) &&
            decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price) && price >= 0
                ? price : throw new ArgumentException("Supply all three nonnegative per-million prices.")).ToArray();
        return new(prices[0], prices[1], prices[2]);
    }
}
