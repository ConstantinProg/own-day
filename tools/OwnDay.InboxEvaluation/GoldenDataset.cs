using System.Text.Json;
using System.Text.Json.Serialization;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Domain.Inbox;

namespace OwnDay.InboxEvaluation;

public sealed record GoldenDataset(string Version, GoldenScenario[] Scenarios)
{
    public static async Task<GoldenDataset> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var dataset = await JsonSerializer.DeserializeAsync<GoldenDataset>(stream, EvaluationJson.Options, cancellationToken)
            ?? throw new InvalidDataException("Missing dataset.");
        dataset.Validate();
        return dataset;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version) || Scenarios is not { Length: > 0 } || Scenarios.Any(x => x is null) ||
            Scenarios.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != Scenarios.Length)
        {
            throw new InvalidDataException("Dataset requires a version and unique scenario IDs.");
        }

        foreach (var scenario in Scenarios)
        {
            if (string.IsNullOrWhiteSpace(scenario.Id) || string.IsNullOrWhiteSpace(scenario.Text) ||
                scenario.Text.Length > InboxAnalysisService.MaxInputCharacters ||
                scenario.CapturedAt.Kind != DateTimeKind.Utc || scenario.Tags is not { Length: > 0 } ||
                scenario.Context is null || scenario.Context.Projects is null || scenario.Context.Projects.Count > InboxAnalysisService.MaxProjects ||
                scenario.Context.Projects.Any(x => x is null || x.Id <= 0 || string.IsNullOrWhiteSpace(x.Title) || x.CreatedAt.Kind != DateTimeKind.Utc) ||
                scenario.Context.Projects.Select(x => x.Id).Distinct().Count() != scenario.Context.Projects.Count ||
                scenario.Variants is not { Length: > 0 } || scenario.ReviewCriteria is null ||
                EvaluationDimensions.All.Any(d => !scenario.ReviewCriteria.TryGetValue(d, out var rubric) || string.IsNullOrWhiteSpace(rubric)))
            {
                throw new InvalidDataException($"Invalid scenario: {scenario.Id}.");
            }

            foreach (var variant in scenario.Variants)
            {
                if (variant is null || string.IsNullOrWhiteSpace(variant.Reason) || variant.Items is not { Length: > 0 and <= 20 })
                {
                    throw new InvalidDataException($"Invalid interpretation: {scenario.Id}.");
                }

                foreach (var item in variant.Items)
                {
                    if (item is null || item.Kinds is not { Length: > 0 } || item.Kinds.Any(k => !Enum.IsDefined(k)) ||
                        item.PropertySets is not { Length: > 0 } || item.PropertySets.Any(p => p is null) ||
                        item.ProjectIdSets is not { Length: > 0 } || item.ProjectIdSets.Any(ids => ids is null || ids.Length > 5 ||
                            ids.Distinct().Count() != ids.Length || ids.Any(id => !scenario.Context.Projects.Any(p => p.Id == id))) ||
                        item.ContentAnchorGroups is null || item.ContentAnchorGroups.Any(group => group is null || group.Length == 0 || group.Any(string.IsNullOrWhiteSpace)))
                    {
                        throw new InvalidDataException($"Invalid expected item: {scenario.Id}.");
                    }

                    // Expectations themselves must satisfy the same production invariants.
                    foreach (var kind in item.Kinds)
                    {
                        foreach (var properties in item.PropertySets)
                        {
                            foreach (var ids in item.ProjectIdSets)
                            {
                                var proposal = new InboxProposal
                                {
                                    Kind = kind, Title = "Golden expectation", Description = null, Properties = properties,
                                    ProjectIds = ids, Ambiguities = item.RequireAmbiguity ? ["Golden ambiguity"] : []
                                };
                                var json = JsonSerializer.Serialize(new InboxAnalysisOutput { Items = [proposal] }, InboxAnalysisJson.Options);
                                if (new InboxAnalysisValidator().ParseAndValidate(json, scenario.Request) is null)
                                {
                                    throw new InvalidDataException($"Expectation violates production contract: {scenario.Id}.");
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

public sealed record GoldenScenario(string Id, string[] Tags, string Text, DateTime CapturedAt,
    InboxAnalysisContext Context, ExpectedVariant[] Variants, Dictionary<string, string> ReviewCriteria)
{
    [JsonIgnore]
    public InboxAnalysisRequest Request => new(Text, CapturedAt, Context, InboxAnalysisService.SupportedTypes);
}

public sealed record ExpectedVariant(string Reason, ExpectedItem[] Items);

// Null properties are expectations of absence, not wildcards. Sets list allowed project alternatives.
// Anchors are deliberately broad lexical diagnostics; they are never a semantic equivalence score.
public sealed record ExpectedItem(InboxTargetKind[] Kinds, InboxProposalProperties[] PropertySets,
    long[][] ProjectIdSets, bool RequireAmbiguity, string[][] ContentAnchorGroups);

public static class EvaluationDimensions
{
    public static readonly string[] All = ["classification", "splitting", "properties", "links", "canonicalization"];
}

public static class EvaluationJson
{
    public static JsonSerializerOptions Options { get; } = new(InboxAnalysisJson.Options)
    {
        WriteIndented = true, MaxDepth = 64, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
    };
    public static JsonSerializerOptions Lines { get; } = new(Options) { WriteIndented = false };
}
