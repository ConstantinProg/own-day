using OwnDay.Domain;

namespace OwnDay.Application.Inbox;

public interface IInboxDraftStore
{
    Task<InboxDraft?> FindAsync(UserId userId, CancellationToken cancellationToken);

    // Stage changes in the caller's transaction alongside domain mutations and reply intent.
    void Add(InboxDraft draft);
    void Remove(InboxDraft draft);
}
