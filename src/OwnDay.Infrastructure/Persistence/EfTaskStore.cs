using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Tasks;
using OwnDay.Domain.Tasks;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfTaskStore(OwnDayDbContext dbContext) : ITaskStore
{
    public async Task<TaskItem> AddAsync(TaskItem task, CancellationToken cancellationToken)
    {
        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync(cancellationToken);
        return task;
    }

    public async Task<IReadOnlyList<TaskItem>> GetActiveAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        await dbContext.Tasks.AsNoTracking()
            .Where(task => task.UserId == userId && task.Status == TaskItemStatus.Active)
            .OrderBy(task => task.CreatedAt)
            .ThenBy(task => task.Id)
            .ToListAsync(cancellationToken);

    public Task<TaskItem?> FindAsync(long taskId, CancellationToken cancellationToken) =>
        dbContext.Tasks.SingleOrDefaultAsync(task => task.Id == taskId, cancellationToken);

    public async Task SaveAsync(CancellationToken cancellationToken) =>
        await dbContext.SaveChangesAsync(cancellationToken);
}
