using OwnDay.Application.Inbox.Analysis;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox.Policy;

public enum InboxPolicyRoute
{
    AutoApply,
    RequireConfirmation,
    KeepInInbox
}

// Names are stable machine-readable codes. Never derive them from model explanations.
public enum InboxPolicyReason
{
    PolicyVersionUnsupported,
    AnalysisUnavailable,
    OwnershipMismatch,
    InboxInactive,
    AnalysisNotCompleted,
    AnalysisSuperseded,
    InputChanged,
    InvalidContext,
    SchemaUnsupported,
    InvalidProposalSet,
    InvalidProposal,
    ProjectUnavailable,
    ProjectChanged,
    ProjectChoiceRequired,
    ProjectLinkRequiresReview,
    ContextTruncated,
    AmbiguityRequiresReview,
    DescriptionNotSupported,
    UnresolvedDateNotSupported,
    WaitingStartRequired,
    WaitingSourceRequiresReview,
    WaitingDateRoleRequiresReview,
    ActionReferenceBoundaryRequiresReview,
    SemanticReviewRequired,
    AutonomousApplicationNotValidated,
    CompositeRequiresJointReview,
    MultipleTargetsNotSupported,
    ProposalBlocked,
    ManualProcessingOnly
}

public sealed record ProposalPolicyDecision(Guid ProposalId, int Position, InboxTargetKind Kind,
    InboxPolicyRoute Route, IReadOnlyList<InboxPolicyReason> Reasons);

// The analysis route is authoritative for the whole batch. Proposal routes are standalone assessments.
// This is an immutable assessment, not a permission token for a later mutation.
public sealed record InboxPolicyDecision(string PolicyVersion, Guid AnalysisId, long InboxItemId,
    int? AnalysisVersion, string StateFingerprint, InboxPolicyRoute Route,
    IReadOnlyList<InboxPolicyReason> Reasons, IReadOnlyList<ProposalPolicyDecision> Proposals);

public sealed record InboxPolicySnapshot(InboxItem Item, InboxAnalysis Analysis, int LatestAnalysisVersion,
    IReadOnlyList<AnalysisProject> ActiveOwnedProjects);

// Prepared and consumed inside the same Inbox transaction; never a persisted permission token.
internal sealed record InboxApplicationPreparation(InboxPolicyDecision Decision,
    IReadOnlyList<PreparedInboxProposal> Proposals);

public interface IInboxPolicyStore
{
    // A coherent, detached, owner-qualified read of the saved analysis and current referenced projects.
    Task<InboxPolicySnapshot?> ReadAsync(UserId userId, long itemId, Guid analysisId, CancellationToken cancellationToken);
}
