using System.Globalization;
using System.Text.Json;
using OwnDay.Domain.Inbox;
using OwnDay.Domain.WaitingFors;

namespace OwnDay.Application.Inbox.Analysis;

// Structural and value validation only. This does not authorize applying any proposal.
public sealed class InboxAnalysisValidator
{
    private const int MaxResponseJsonCharacters = 64_000;
    // Preserve the single-proposal budget within the response's {"items":[]} envelope.
    private const int MaxProposalJsonCharacters = MaxResponseJsonCharacters - 12;

    public InboxAnalysisOutput? ParseAndValidate(string json, InboxAnalysisRequest request)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxResponseJsonCharacters)
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
        return output.Items.All(item => IsValidProposal(item, request, projects)) ? output : null;
    }

    public InboxProposal? ParseAndValidateProposal(string json, InboxAnalysisRequest request)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxProposalJsonCharacters)
        {
            return null;
        }

        InboxProposal? proposal;
        try
        {
            proposal = JsonSerializer.Deserialize<InboxProposal>(json, InboxAnalysisJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        var projects = request.Context.Projects.Select(project => project.Id).ToHashSet();
        return IsValidProposal(proposal, request, projects) ? proposal : null;
    }

    private static bool IsValidProposal(InboxProposal? item, InboxAnalysisRequest request, HashSet<long> projects)
    {
        if (item is null || !Enum.IsDefined(item.Kind) ||
            request.SupportedTypes.SingleOrDefault(type => type.Kind == item.Kind) is not { } supported)
        {
            return false;
        }

        return RequiredText(item.Title, supported.MaxTitleLength) && OptionalText(item.Description, 2000) &&
            HasValidAmbiguities(item.Ambiguities) && HasValidProjects(item, projects) &&
            HasValidProperties(item, request);
    }

    private static bool HasValidAmbiguities(string[]? ambiguities) =>
        ambiguities is { Length: <= 10 } && ambiguities.All(text => RequiredText(text, 500));

    private static bool HasValidProjects(InboxProposal item, HashSet<long> projects)
    {
        var ids = item.ProjectIds;
        return ids is { Length: <= 5 } && ids.Distinct().Count() == ids.Length &&
            ids.All(projects.Contains) &&
            (item.Kind != InboxTargetKind.Project || ids.Length == 0) &&
            (ids.Length <= 1 || item.Ambiguities.Length > 0);
    }

    private static bool HasValidProperties(InboxProposal item, InboxAnalysisRequest request)
    {
        if (item.Properties is not { } properties ||
            !OptionalText(properties.Source, WaitingFor.MaxSourceLength) ||
            !OptionalText(properties.UnresolvedDateText, 500) ||
            !OptionalText(properties.WaitingSinceEvidence, 100))
        {
            return false;
        }

        if (properties.Source is { } source && !request.OriginalText.Contains(source, StringComparison.Ordinal))
        {
            return false;
        }

        if (item.Kind != InboxTargetKind.WaitingFor &&
            (properties.Source is not null || properties.WaitingSince is not null || properties.WaitingSinceEvidence is not null))
        {
            return false;
        }

        if (properties.UnresolvedDateText is { } unresolved &&
            (!request.OriginalText.Contains(unresolved, StringComparison.Ordinal) || item.Ambiguities.Length == 0))
        {
            return false;
        }

        return HasValidWaitingSince(properties, request);
    }

    private static bool HasValidWaitingSince(InboxProposalProperties properties, InboxAnalysisRequest request)
    {
        if (properties.WaitingSince is not { } since)
        {
            return properties.WaitingSinceEvidence is null;
        }

        // Require explicit time and offset, also when a future caller supplies a known timezone.
        var evidence = properties.WaitingSinceEvidence;
        return evidence is not null && request.OriginalText.Contains(evidence, StringComparison.Ordinal) &&
            HasExplicitOffset(evidence) &&
            DateTimeOffset.TryParse(evidence, CultureInfo.InvariantCulture, DateTimeStyles.None, out var explicitDate) &&
            explicitDate == since && since.Offset == TimeSpan.Zero && since.UtcDateTime <= request.CapturedAt &&
            properties.UnresolvedDateText is null;
    }

    private static bool HasExplicitOffset(string text) => text.Contains('T') &&
        (text.EndsWith('Z') || (text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-'));

    private static bool RequiredText(string? text, int max) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Contains('\0') && text == text.Trim();

    private static bool OptionalText(string? text, int max) => text is null || RequiredText(text, max);
}
