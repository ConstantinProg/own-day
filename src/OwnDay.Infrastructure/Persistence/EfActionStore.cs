using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Actions;
using OwnDay.Domain;
using OwnDay.Domain.Actions;
using Action = OwnDay.Domain.Actions.Action;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfActionStore(OwnDayDbContext dbContext) : IActionStore
{
    public async Task<Action> AddAsync(Action action, CancellationToken cancellationToken)
    {
        dbContext.Actions.Add(action);
        await dbContext.SaveChangesAsync(cancellationToken);
        return action;
    }

    public async Task<IReadOnlyList<Action>> GetActiveAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        await dbContext.Actions.AsNoTracking()
            .Where(action => action.UserId == userId && action.Status == ActionStatus.Active)
            .OrderBy(action => action.CreatedAt)
            .ThenBy(action => action.Id)
            .ToListAsync(cancellationToken);

    public Task<Action?> FindAsync(long actionId, CancellationToken cancellationToken) =>
        dbContext.Actions.SingleOrDefaultAsync(action => action.Id == actionId, cancellationToken);

    public async Task SaveAsync(CancellationToken cancellationToken) =>
        await dbContext.SaveChangesAsync(cancellationToken);
}
