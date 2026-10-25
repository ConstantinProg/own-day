using OwnDay.Domain;

namespace OwnDay.Domain.Inbox;

public sealed class InboxItem
{
    private readonly List<InboxTarget> _targets = [];
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
    public IReadOnlyList<InboxTarget> Targets => _targets.AsReadOnly();

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
        => Status == InboxItemStatus.Active && ProcessMany([new InboxTarget(targetKind, targetId)], processedAt);

    public bool ProcessMany(IReadOnlyList<InboxTarget> targets, DateTime processedAt)
    {
        if (Status != InboxItemStatus.Active)
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0 || targets.Any(t => t is null) ||
            targets.DistinctBy(t => (t.Kind, t.TargetId)).Count() != targets.Count)
        {
            throw new ArgumentException("Distinct targets are required.", nameof(targets));
        }

        DomainTime.RequireNotBefore(processedAt, CapturedAt, nameof(processedAt));

        // Legacy fields identify the first target, including for existing manual consumers.
        TargetKind = targets[0].Kind;
        TargetId = targets[0].TargetId;
        _targets.AddRange(targets);
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
