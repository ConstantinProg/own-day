using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox;

public sealed record ProcessInboxItem(
    long InboxItemId,
    InboxTargetKind TargetKind,
    string Text,
    long? ProjectId = null,
    string? Source = null,
    DateTime? WaitingSince = null);
