using OwnDay.Domain.Tasks;

namespace OwnDay.Application.Tasks;

public sealed class TaskService
{
    private readonly ITaskStore _store;
    private readonly TimeProvider _timeProvider;

    public TaskService(ITaskStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task<TaskItem> AddAsync(UserId userId, string title, CancellationToken cancellationToken)
    {
        var task = TaskItem.Create(userId, title, _timeProvider.GetUtcNow().UtcDateTime);
        return await _store.AddAsync(task, cancellationToken);
    }

    public Task<IReadOnlyList<TaskItem>> GetActiveAsync(UserId userId, CancellationToken cancellationToken) =>
        _store.GetActiveAsync(userId, cancellationToken);

    public async Task<CompleteTaskResult> CompleteAsync(
        UserId userId,
        long taskId,
        CancellationToken cancellationToken)
    {
        var task = await _store.FindAsync(taskId, cancellationToken);
        if (task is null || task.UserId != userId)
        {
            return CompleteTaskResult.NotFound;
        }

        if (!task.Complete(_timeProvider.GetUtcNow().UtcDateTime))
        {
            return CompleteTaskResult.AlreadyCompleted;
        }

        await _store.SaveAsync(cancellationToken);
        return CompleteTaskResult.Completed;
    }
}
