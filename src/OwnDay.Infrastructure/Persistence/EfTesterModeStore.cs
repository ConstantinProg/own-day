using Microsoft.EntityFrameworkCore;
using OwnDay.Application.TesterMode;
using OwnDay.Domain;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfTesterModeStore(OwnDayDbContext db) : ITesterModeStore
{
    public Task<TesterModeState?> FindAsync(UserId userId, CancellationToken cancellationToken) =>
        db.Set<TesterModeState>().SingleOrDefaultAsync(state => state.UserId == userId, cancellationToken);

    public void Add(TesterModeState state) => db.Add(state);
}
