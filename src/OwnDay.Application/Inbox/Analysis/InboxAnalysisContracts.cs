using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox.Analysis;

public sealed record AnalysisProject(long Id, string Title, DateTime CreatedAt);

public sealed record InboxAnalysisContext(
    string? TimeZoneId,
    IReadOnlyList<AnalysisProject> Projects,
    bool ProjectsTruncated);

public sealed record SupportedInboxType(InboxTargetKind Kind, int MaxTitleLength, string[] Properties);

public sealed record InboxAnalysisRequest(
    string OriginalText,
    DateTime CapturedAt,
    InboxAnalysisContext Context,
    IReadOnlyList<SupportedInboxType> SupportedTypes);

public sealed record InboxAnalyzerDescriptor(string Model, string SchemaVersion, string PromptVersion,
    string? ReasoningEffort = null, int? MaxOutputTokens = null, int? TimeoutSeconds = null);

public sealed record InboxAnalyzerResponse(
    string Json,
    string Model,
    string? RequestId = null,
    int? InputTokens = null,
    int? OutputTokens = null);

public interface IInboxAnalyzer
{
    InboxAnalyzerDescriptor Descriptor { get; }
    Task<InboxAnalyzerResponse> AnalyzeAsync(InboxAnalysisRequest request, CancellationToken cancellationToken);
}

// Only safe diagnostic codes cross the provider boundary; never provider exception messages or response bodies.
public sealed class InboxAnalyzerException(InboxAnalysisCode code) : Exception("Inbox analysis provider failed.")
{
    public InboxAnalysisCode Code { get; } = code;
}

public sealed record InboxAnalysisOutput
{
    public required InboxProposal[] Items { get; init; }
}

public sealed record InboxProposal
{
    public required InboxTargetKind Kind { get; init; }
    public required string Title { get; init; }
    public required string? Description { get; init; }
    public required InboxProposalProperties Properties { get; init; }
    public required long[] ProjectIds { get; init; }
    public required string[] Ambiguities { get; init; }
}

public sealed record InboxProposalProperties
{
    public required string? Source { get; init; }
    // An instant may only be extracted from explicit ISO 8601 text with an offset, never from a relative date.
    public required DateTimeOffset? WaitingSince { get; init; }
    public required string? WaitingSinceEvidence { get; init; }
    public required string? UnresolvedDateText { get; init; }
}
