namespace OwnDay.Domain.Inbox;

public sealed class InboxTarget
{
    private InboxTarget() { }

    public InboxTarget(InboxTargetKind kind, long targetId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetId);
        Kind = kind;
        TargetId = targetId;
    }

    public InboxTargetKind Kind { get; private set; }
    public long TargetId { get; private set; }
}
