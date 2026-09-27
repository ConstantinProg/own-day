using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfInboxProcessingStore(OwnDayDbContext dbContext) : IInboxProcessingStore
{
    public async Task<TResult> ExecuteLockedAsync<TResult>(
        UserId userId,
        long itemId,
        Func<InboxItem?, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        // A webhook already owns its transaction. Direct callers get the same atomic boundary here.
        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        InboxItem? item;
        if (dbContext.Database.IsNpgsql())
        {
            var rows = await dbContext.InboxItems.FromSqlInterpolated(
                $"SELECT * FROM inbox_items WHERE id = {itemId} AND user_id = {userId.Value} FOR UPDATE")
                .ToListAsync(cancellationToken);
            item = rows.SingleOrDefault();
        }
        else
        {
            // SQLite fixtures exercise workflow behavior; PostgreSQL verifies row locking.
            item = await dbContext.InboxItems.SingleOrDefaultAsync(
                row => row.Id == itemId && row.UserId == userId, cancellationToken);
        }

        if (item is not null)
        {
            await dbContext.Entry(item).ReloadAsync(cancellationToken);
        }

        var result = await operation(item, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }
}
