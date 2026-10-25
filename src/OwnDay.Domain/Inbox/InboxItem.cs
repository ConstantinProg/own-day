using OwnDay.Domain;

namespace OwnDay.Domain.Inbox;

public sealed class InboxItem
{
    private InboxItem() { }

    private InboxItem(UserId userId, string originalText, DateTime capturedAt)
    {
        UserId = userId;
        OriginalText = originalText;
        CapturedAt = capturedAt;
        Status = InboxItemStatus.Active;
    }

    public long Id { get; private set; }
    public UserId UserId { get; private set; }
    public string OriginalText { get; private set; } = string.Empty;
    public DateTime CapturedAt { get; private set; }
    public InboxItemStatus Status { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public DateTime? DiscardedAt { get; private set; }
    public InboxTargetKind? TargetKind { get; private set; }
    public long? TargetId { get; private set; }

    public static InboxItem Capture(UserId userId, string originalText, DateTime capturedAt)
    {
        if (!userId.IsValid)
        {
            throw new ArgumentException("A valid user is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(originalText))
        {
            throw new ArgumentException("Original text is required.", nameof(originalText));
        }

        DomainTime.RequireUtc(capturedAt, nameof(capturedAt));
        return new InboxItem(userId, originalText, capturedAt);
    }

    public bool Process(InboxTargetKind targetKind, long targetId, DateTime processedAt)
    {
        if (Status != InboxItemStatus.Active)
        {
            return false;
        }

        if (!Enum.IsDefined(targetKind))
        {
            throw new ArgumentOutOfRangeException(nameof(targetKind));
        }

        if (targetId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetId));
        }

        DomainTime.RequireNotBefore(processedAt, CapturedAt, nameof(processedAt));

        TargetKind = targetKind;
        TargetId = targetId;
        ProcessedAt = processedAt;
        Status = InboxItemStatus.Processed;
        return true;
    }

    public bool Discard(DateTime discardedAt)
    {
        if (Status != InboxItemStatus.Active)
        {
            return false;
        }

        DomainTime.RequireNotBefore(discardedAt, CapturedAt, nameof(discardedAt));

        DiscardedAt = discardedAt;
        Status = InboxItemStatus.Discarded;
        return true;
    }
}
