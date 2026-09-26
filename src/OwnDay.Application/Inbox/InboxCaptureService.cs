using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox;

public sealed class InboxCaptureService(IInboxItemStore store, TimeProvider timeProvider)
{
    public Task<InboxItem> CaptureAsync(UserId userId, string originalText, CancellationToken cancellationToken) =>
        store.AddAsync(InboxItem.Capture(userId, originalText, timeProvider.GetUtcNow().UtcDateTime), cancellationToken);

    public Task<IReadOnlyList<InboxItem>> GetActiveAsync(UserId userId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId.Value);
        return store.GetActiveAsync(userId, cancellationToken);
    }

    public Task<InboxItem?> FindAsync(UserId userId, long itemId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId.Value);
        if (itemId <= 0)
        {
            return Task.FromResult<InboxItem?>(null);
        }

        return store.FindAsync(userId, itemId, cancellationToken);
    }
}
