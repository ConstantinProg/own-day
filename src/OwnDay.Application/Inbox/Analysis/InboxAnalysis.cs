using System.Text.Json;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox.Analysis;

public enum InboxAnalysisStatus
{
    Pending,
    Completed,
    InvalidResponse,
    Failed
}

// Application workflow history. It has no authority over Inbox or target entity state.
public sealed class InboxAnalysis
{
    private InboxAnalysis() { }

    public Guid Id { get; private set; }
    public long InboxItemId { get; private set; }
    public UserId UserId { get; private set; }
    public int Version { get; private set; }
    public InboxAnalysisStatus Status { get; private set; }
    public string Model { get; private set; } = string.Empty;
    public string SchemaVersion { get; private set; } = string.Empty;
    public string PromptVersion { get; private set; } = string.Empty;
    public string ContextJson { get; private set; } = string.Empty;
    public string ContextFingerprint { get; private set; } = string.Empty;
    public string InputFingerprint { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string DiagnosticsJson { get; private set; } = "{}";
    public List<InboxAnalysisProposal> Proposals { get; private set; } = [];

    public static InboxAnalysis Start(UserId userId, long itemId, int version,
        InboxAnalyzerDescriptor descriptor, string contextJson, string contextFingerprint,
        string inputFingerprint, DateTime createdAt) => new()
        {
            Id = Guid.NewGuid(), UserId = userId, InboxItemId = itemId, Version = version,
            Model = descriptor.Model, SchemaVersion = descriptor.SchemaVersion, PromptVersion = descriptor.PromptVersion,
            ContextJson = contextJson, ContextFingerprint = contextFingerprint, InputFingerprint = inputFingerprint,
            CreatedAt = createdAt, Status = InboxAnalysisStatus.Pending
        };

    public void Finish(InboxAnalysisStatus status, DateTime completedAt, string diagnosticsJson,
        InboxAnalysisOutput? output = null, string? actualModel = null)
    {
        if (Status != InboxAnalysisStatus.Pending || status == InboxAnalysisStatus.Pending || !Enum.IsDefined(status) ||
            (status == InboxAnalysisStatus.Completed) != (output is not null) || completedAt < CreatedAt)
        {
            throw new InvalidOperationException("Invalid analysis transition.");
        }

        if (output is not null)
        {
            Proposals = output.Items.Select((item, position) => new InboxAnalysisProposal(
                Id, position, item)).ToList();
        }

        Status = status;
        CompletedAt = completedAt;
        DiagnosticsJson = diagnosticsJson;
        Model = actualModel ?? Model;
    }
}

public sealed class InboxAnalysisProposal
{
    private InboxAnalysisProposal() { }

    internal InboxAnalysisProposal(Guid analysisId, int position, InboxProposal proposal)
    {
        Id = Guid.NewGuid();
        AnalysisId = analysisId;
        Position = position;
        Kind = proposal.Kind;
        Title = proposal.Title;
        PayloadJson = JsonSerializer.Serialize(proposal, InboxAnalysisJson.Options);
    }

    public Guid Id { get; private set; }
    public Guid AnalysisId { get; private set; }
    public int Position { get; private set; }
    public InboxTargetKind Kind { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;

    public InboxProposal ReadProposal() =>
        JsonSerializer.Deserialize<InboxProposal>(PayloadJson, InboxAnalysisJson.Options)!;
}
