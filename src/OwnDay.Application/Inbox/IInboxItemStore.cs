using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox;

public interface IInboxItemStore
{
    Task<InboxItem> AddAsync(InboxItem item, CancellationToken cancellationToken);
    Task<IReadOnlyList<InboxItem>> GetActiveAsync(UserId userId, CancellationToken cancellationToken);
    Task<InboxItem?> FindAsync(UserId userId, long itemId, CancellationToken cancellationToken);
}
