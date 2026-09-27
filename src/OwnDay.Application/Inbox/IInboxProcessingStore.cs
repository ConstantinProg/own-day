using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox;

public interface IInboxProcessingStore
{
    Task<TResult> ExecuteLockedAsync<TResult>(
        UserId userId,
        long itemId,
        Func<InboxItem?, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken);
}
