using OwnDay.Domain;
using OwnDay.Domain.Actions;
using Action = OwnDay.Domain.Actions.Action;

namespace OwnDay.Application.Actions;

public sealed class ActionService
{
    private readonly IActionStore _store;
    private readonly TimeProvider _timeProvider;

    public ActionService(IActionStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task<Action> AddAsync(UserId userId, string title, CancellationToken cancellationToken)
    {
        var action = Action.Create(userId, title, _timeProvider.GetUtcNow().UtcDateTime);
        return await _store.AddAsync(action, cancellationToken);
    }

    public Task<IReadOnlyList<Action>> GetActiveAsync(UserId userId, CancellationToken cancellationToken) =>
        _store.GetActiveAsync(userId, cancellationToken);

    public async Task<CompleteActionResult> CompleteAsync(
        UserId userId,
        long actionId,
        CancellationToken cancellationToken)
    {
        var action = await _store.FindAsync(actionId, cancellationToken);
        if (action is null || action.UserId != userId)
        {
            return CompleteActionResult.NotFound;
        }

        if (!action.Complete(_timeProvider.GetUtcNow().UtcDateTime))
        {
            return CompleteActionResult.AlreadyCompleted;
        }

        await _store.SaveAsync(cancellationToken);
        return CompleteActionResult.Completed;
    }
}
