using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox;
using OwnDay.Domain;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfInboxDraftStore(OwnDayDbContext dbContext) : IInboxDraftStore
{
    public Task<InboxDraft?> FindAsync(UserId userId, CancellationToken cancellationToken) =>
        dbContext.TelegramInboxDrafts.SingleOrDefaultAsync(draft => draft.UserId == userId.Value, cancellationToken);

    public void Add(InboxDraft draft) => dbContext.TelegramInboxDrafts.Add(draft);

    public void Remove(InboxDraft draft) => dbContext.TelegramInboxDrafts.Remove(draft);
}
