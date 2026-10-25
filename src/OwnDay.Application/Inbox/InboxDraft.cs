using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox;

public sealed class InboxDraft
{
    public long UserId { get; set; }
    public long InboxItemId { get; set; }
    public InboxDraftStep Step { get; set; }
    public InboxTargetKind? TargetKind { get; set; }
    public string? Text { get; set; }
    public string? Source { get; set; }
    public long? ProjectId { get; set; }
    public long Version { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public enum InboxDraftStep
{
    Target,
    Text,
    Source,
    Project,
    Confirm
}
