using OwnDay.Application.Inbox.Observability;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfInboxTelemetry(OwnDayDbContext db) : IInboxTelemetry
{
    public void Record(UserId userId, long itemId, string code, DateTime at, InboxTargetKind? kind = null, long? targetId = null) =>
        db.InboxOperationEvents.Add(new()
        {
            UserId = userId.Value, InboxItemId = itemId, Code = code, At = at, Kind = kind, TargetId = targetId
        });
}
