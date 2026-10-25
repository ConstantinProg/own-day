using System.Globalization;
using System.Text.Json;
using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox.Analysis;

// Structural and value validation only. This does not authorize applying any proposal.
public sealed class InboxAnalysisValidator
{
    public InboxAnalysisOutput? ParseAndValidate(string json, InboxAnalysisRequest request)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64_000)
        {
            return null;
        }

        InboxAnalysisOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<InboxAnalysisOutput>(json, InboxAnalysisJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (output?.Items is not { Length: > 0 and <= 20 })
        {
            return null;
        }

        var projects = request.Context.Projects.Select(project => project.Id).ToHashSet();
        foreach (var item in output.Items)
        {
            if (item is null || !Enum.IsDefined(item.Kind) ||
                request.SupportedTypes.SingleOrDefault(type => type.Kind == item.Kind) is not { } supported ||
                !RequiredText(item.Title, supported.MaxTitleLength) || !OptionalText(item.Description, 2000) ||
                item.Properties is not { } properties || item.ProjectIds is null || item.Ambiguities is null ||
                item.ProjectIds.Length > 5 || item.ProjectIds.Distinct().Count() != item.ProjectIds.Length ||
                item.ProjectIds.Any(id => !projects.Contains(id)) ||
                (item.Kind == InboxTargetKind.Project && item.ProjectIds.Length != 0) ||
                item.Ambiguities.Length > 10 || item.Ambiguities.Any(text => !RequiredText(text, 500)) ||
                (item.ProjectIds.Length > 1 && item.Ambiguities.Length == 0) ||
                !OptionalText(properties.Source, 200) ||
                !OptionalText(properties.UnresolvedDateText, 500) ||
                !OptionalText(properties.WaitingSinceEvidence, 100) ||
                (properties.Source is not null && !request.OriginalText.Contains(properties.Source, StringComparison.Ordinal)) ||
                (item.Kind != InboxTargetKind.WaitingFor &&
                    (properties.Source is not null || properties.WaitingSince is not null || properties.WaitingSinceEvidence is not null)))
            {
                return null;
            }

            if (properties.UnresolvedDateText is { } unresolved &&
                (!request.OriginalText.Contains(unresolved, StringComparison.Ordinal) || item.Ambiguities.Length == 0))
            {
                return null;
            }

            if (properties.WaitingSince is { } since)
            {
                var evidence = properties.WaitingSinceEvidence;
                // Require explicit time and offset, also when a future caller supplies a known timezone.
                if (evidence is null || !request.OriginalText.Contains(evidence, StringComparison.Ordinal) ||
                    !HasExplicitOffset(evidence) ||
                    !DateTimeOffset.TryParse(evidence, CultureInfo.InvariantCulture, DateTimeStyles.None, out var explicitDate) ||
                    explicitDate != since || since.Offset != TimeSpan.Zero || since.UtcDateTime > request.CapturedAt ||
                    properties.UnresolvedDateText is not null)
                {
                    return null;
                }
            }
            else if (properties.WaitingSinceEvidence is not null)
            {
                return null;
            }
        }

        return output;
    }

    private static bool HasExplicitOffset(string text) => text.Contains('T') &&
        (text.EndsWith('Z') || (text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-'));

    private static bool RequiredText(string? text, int max) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Contains('\0') && text == text.Trim();

    private static bool OptionalText(string? text, int max) => text is null || RequiredText(text, max);
}
