using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox;

public enum InboxDraftOutcome
{
    Prompt,
    Captured,
    Expired,
    Unavailable,
    ProcessingUnavailable,
    NotFound,
    InvalidText,
    InvalidSource,
    InvalidProject,
    ProjectUnavailable,
    InvalidTargetData,
    Processed
}

public sealed record InboxDraftResult(
    InboxDraftOutcome Outcome,
    InboxDraft? Draft = null,
    InboxItem? Item = null,
    long? TargetId = null);
