using OwnDay.Application.Tasks;
using OwnDay.Domain.Tasks;
using Xunit;

namespace OwnDay.UnitTests.Tasks;

public sealed class TaskServiceTests
{
    private static readonly UserId Alice = new(1);
    private static readonly UserId Bob = new(2);
    private static readonly DateTime Now = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AddAsync_ValidTitle_PersistsActiveTask()
    {
        var store = new RecordingTaskStore();
        var service = CreateService(store);

        var task = await service.AddAsync(Alice, "Buy groceries", CancellationToken.None);

        Assert.Same(task, Assert.Single(store.Tasks));
        Assert.Equal(Alice, task.UserId);
        Assert.Equal(TaskItemStatus.Active, task.Status);
    }

    [Fact]
    public async Task GetActiveAsync_MultipleUsers_ReturnsOnlyOwnActiveTasks()
    {
        var store = new RecordingTaskStore();
        var service = CreateService(store);
        var aliceTask = await service.AddAsync(Alice, "Alice task", CancellationToken.None);
        await service.AddAsync(Bob, "Bob task", CancellationToken.None);
        var completed = await service.AddAsync(Alice, "Done task", CancellationToken.None);
        completed.Complete(Now);

        var tasks = await service.GetActiveAsync(Alice, CancellationToken.None);

        Assert.Equal(aliceTask, Assert.Single(tasks));
    }

    [Fact]
    public async Task CompleteAsync_OwnTask_CompletesTask()
    {
        var store = new RecordingTaskStore();
        var service = CreateService(store);
        await service.AddAsync(Alice, "My task", CancellationToken.None);

        var result = await service.CompleteAsync(Alice, 1, CancellationToken.None);

        Assert.Equal(CompleteTaskResult.Completed, result);
        Assert.Equal(TaskItemStatus.Completed, store.Tasks[0].Status);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(CompleteTaskResult.AlreadyCompleted,
            await service.CompleteAsync(Alice, 1, CancellationToken.None));
    }

    [Fact]
    public async Task CompleteAsync_OtherUsersTask_ReturnsNotFoundWithoutMutation()
    {
        var store = new RecordingTaskStore();
        var service = CreateService(store);
        await service.AddAsync(Bob, "Private task", CancellationToken.None);

        var result = await service.CompleteAsync(Alice, 1, CancellationToken.None);

        Assert.Equal(CompleteTaskResult.NotFound, result);
        Assert.Equal(TaskItemStatus.Active, store.Tasks[0].Status);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task CompleteAsync_MissingTask_ReturnsNotFound()
    {
        var result = await CreateService(new RecordingTaskStore())
            .CompleteAsync(Alice, 99, CancellationToken.None);

        Assert.Equal(CompleteTaskResult.NotFound, result);
    }

    [Fact]
    public async Task TaskOperations_CallerToken_ReachesStore()
    {
        var store = new RecordingTaskStore();
        var service = CreateService(store);
        using var cancellation = new CancellationTokenSource();

        await service.AddAsync(Alice, "My task", cancellation.Token);
        Assert.Equal(cancellation.Token, store.LastToken);

        await service.GetActiveAsync(Alice, cancellation.Token);
        Assert.Equal(cancellation.Token, store.LastToken);

        await service.CompleteAsync(Alice, 1, cancellation.Token);
        Assert.Equal(cancellation.Token, store.LastToken);
    }

    private static TaskService CreateService(RecordingTaskStore store) =>
        new(store, new FixedTimeProvider(Now));

    private sealed class RecordingTaskStore : ITaskStore
    {
        public List<TaskItem> Tasks { get; } = [];
        public int SaveCount { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<TaskItem> AddAsync(TaskItem task, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            Tasks.Add(task);
            return Task.FromResult(task);
        }

        public Task<IReadOnlyList<TaskItem>> GetActiveAsync(UserId userId, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            return Task.FromResult<IReadOnlyList<TaskItem>>(
                Tasks.Where(task => task.UserId == userId && task.Status == TaskItemStatus.Active).ToList());
        }

        public Task<TaskItem?> FindAsync(long taskId, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            return Task.FromResult(Tasks.ElementAtOrDefault(checked((int)taskId - 1)));
        }

        public Task SaveAsync(CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            SaveCount++;
            return Task.CompletedTask;
        }
    }
}
