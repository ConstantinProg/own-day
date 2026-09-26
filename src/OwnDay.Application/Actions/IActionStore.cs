using OwnDay.Domain;
using OwnDay.Domain.Actions;
using Action = OwnDay.Domain.Actions.Action;

namespace OwnDay.Application.Actions;

public interface IActionStore
{
    Task<bool> ProjectBelongsToAsync(long projectId, UserId userId, CancellationToken cancellationToken);
    Task<Action> AddAsync(Action action, CancellationToken cancellationToken);
    Task<IReadOnlyList<Action>> GetActiveAsync(UserId userId, CancellationToken cancellationToken);
    Task<Action?> FindAsync(UserId userId, long actionId, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}
