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

    public Task<Action> AddAsync(UserId userId, string title, CancellationToken cancellationToken) =>
        AddAsync(userId, title, null, cancellationToken);

    public async Task<Action> AddAsync(UserId userId, string title, long? projectId, CancellationToken cancellationToken)
    {
        var action = Action.Create(userId, title, _timeProvider.GetUtcNow().UtcDateTime, projectId);
        if (projectId is long id && !await _store.ProjectBelongsToAsync(id, userId, cancellationToken))
        {
            throw new ArgumentException("Project was not found for this user.", nameof(projectId));
        }

        return await _store.AddAsync(action, cancellationToken);
    }

    public Task<IReadOnlyList<Action>> GetActiveAsync(UserId userId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId.Value);
        return _store.GetActiveAsync(userId, cancellationToken);
    }

    public async Task<CompleteActionResult> CompleteAsync(
        UserId userId,
        long actionId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId.Value);
        var action = await _store.FindAsync(userId, actionId, cancellationToken);
        if (action is null)
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
