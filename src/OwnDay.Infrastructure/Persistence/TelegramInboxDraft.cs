using OwnDay.Domain.Inbox;

namespace OwnDay.Infrastructure.Persistence;

public sealed class TelegramInboxDraft
{
    public long UserId { get; set; }
    public long InboxItemId { get; set; }
    public TelegramInboxDraftStep Step { get; set; }
    public InboxTargetKind? TargetKind { get; set; }
    public string? Text { get; set; }
    public string? Source { get; set; }
    public long? ProjectId { get; set; }
    public long Version { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public enum TelegramInboxDraftStep
{
    Target,
    Text,
    Source,
    Project,
    Confirm
}
