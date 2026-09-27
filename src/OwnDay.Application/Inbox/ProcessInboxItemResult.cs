using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox;

public enum ProcessInboxItemStatus
{
    Processed,
    NotFound,
    AlreadyProcessed,
    Discarded,
    InvalidTargetData,
    InvalidProject
}

public sealed record ProcessInboxItemResult(
    ProcessInboxItemStatus Status,
    InboxTargetKind? TargetKind = null,
    long? TargetId = null);
