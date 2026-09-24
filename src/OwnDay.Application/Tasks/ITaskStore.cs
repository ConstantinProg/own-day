using OwnDay.Domain.Tasks;

namespace OwnDay.Application.Tasks;

public interface ITaskStore
{
    Task<TaskItem> AddAsync(TaskItem task, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskItem>> GetActiveAsync(UserId userId, CancellationToken cancellationToken);
    Task<TaskItem?> FindAsync(long taskId, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}
