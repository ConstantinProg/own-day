using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfInboxItemStore(OwnDayDbContext dbContext) : IInboxItemStore
{
    public async Task<InboxItem> AddAsync(InboxItem item, CancellationToken cancellationToken)
    {
        dbContext.InboxItems.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task<IReadOnlyList<InboxItem>> GetActiveAsync(UserId userId, CancellationToken cancellationToken) =>
        await dbContext.InboxItems.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == InboxItemStatus.Active)
            .OrderBy(item => item.CapturedAt)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

    public Task<InboxItem?> FindAsync(UserId userId, long itemId, CancellationToken cancellationToken) =>
        dbContext.InboxItems.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.Id == itemId, cancellationToken);
}
