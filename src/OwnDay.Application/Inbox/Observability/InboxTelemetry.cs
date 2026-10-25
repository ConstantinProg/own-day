using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox.Observability;

// Enlisted in the existing Inbox transaction, including Telegram update deduplication.
public interface IInboxTelemetry
{
    void Record(UserId userId, long itemId, string code, DateTime at, InboxTargetKind? kind = null, long? targetId = null);
}

public sealed class InboxOperationEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long UserId { get; init; }
    public long InboxItemId { get; init; }
    public string Code { get; init; } = string.Empty;
    public DateTime At { get; init; }
    public InboxTargetKind? Kind { get; init; }
    public long? TargetId { get; init; }
}
