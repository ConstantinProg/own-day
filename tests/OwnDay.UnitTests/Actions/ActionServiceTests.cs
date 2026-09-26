using OwnDay.Application.Actions;
using OwnDay.Domain;
using OwnDay.Domain.Actions;
using Action = OwnDay.Domain.Actions.Action;
using Xunit;

namespace OwnDay.UnitTests.Actions;

public sealed class ActionServiceTests
{
    private static readonly UserId Alice = new(1);
    private static readonly UserId Bob = new(2);
    private static readonly DateTime Now = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AddAsync_ValidTitle_PersistsActiveAction()
    {
        var store = new RecordingActionStore();
        var service = CreateService(store);

        var action = await service.AddAsync(Alice, "Buy groceries", CancellationToken.None);

        Assert.Same(action, Assert.Single(store.Actions));
        Assert.Equal(Alice, action.UserId);
        Assert.Equal(ActionStatus.Active, action.Status);
    }

    [Fact]
    public async Task GetActiveAsync_MultipleUsers_ReturnsOnlyOwnActiveActions()
    {
        var store = new RecordingActionStore();
        var service = CreateService(store);
        var aliceAction = await service.AddAsync(Alice, "Alice action", CancellationToken.None);
        await service.AddAsync(Bob, "Bob action", CancellationToken.None);
        var completed = await service.AddAsync(Alice, "Done action", CancellationToken.None);
        completed.Complete(Now);

        var actions = await service.GetActiveAsync(Alice, CancellationToken.None);

        Assert.Equal(aliceAction, Assert.Single(actions));
    }

    [Fact]
    public async Task CompleteAsync_OwnAction_CompletesAction()
    {
        var store = new RecordingActionStore();
        var service = CreateService(store);
        await service.AddAsync(Alice, "My action", CancellationToken.None);

        var result = await service.CompleteAsync(Alice, 1, CancellationToken.None);

        Assert.Equal(CompleteActionResult.Completed, result);
        Assert.Equal(ActionStatus.Completed, store.Actions[0].Status);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(CompleteActionResult.AlreadyCompleted,
            await service.CompleteAsync(Alice, 1, CancellationToken.None));
    }

    [Fact]
    public async Task CompleteAsync_OtherUsersAction_ReturnsNotFoundWithoutMutation()
    {
        var store = new RecordingActionStore();
        var service = CreateService(store);
        await service.AddAsync(Bob, "Private action", CancellationToken.None);

        var result = await service.CompleteAsync(Alice, 1, CancellationToken.None);

        Assert.Equal(CompleteActionResult.NotFound, result);
        Assert.Equal(ActionStatus.Active, store.Actions[0].Status);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task CompleteAsync_MissingAction_ReturnsNotFound()
    {
        var result = await CreateService(new RecordingActionStore())
            .CompleteAsync(Alice, 99, CancellationToken.None);

        Assert.Equal(CompleteActionResult.NotFound, result);
    }

    [Fact]
    public async Task ActionOperations_CallerToken_ReachesStore()
    {
        var store = new RecordingActionStore();
        var service = CreateService(store);
        using var cancellation = new CancellationTokenSource();

        await service.AddAsync(Alice, "My action", cancellation.Token);
        Assert.Equal(cancellation.Token, store.LastToken);

        await service.GetActiveAsync(Alice, cancellation.Token);
        Assert.Equal(cancellation.Token, store.LastToken);

        await service.CompleteAsync(Alice, 1, cancellation.Token);
        Assert.Equal(cancellation.Token, store.LastToken);
    }

    private static ActionService CreateService(RecordingActionStore store) =>
        new(store, new FixedTimeProvider(Now));

    private sealed class RecordingActionStore : IActionStore
    {
        public List<Action> Actions { get; } = [];
        public int SaveCount { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<Action> AddAsync(Action action, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            Actions.Add(action);
            return Task.FromResult(action);
        }

        public Task<IReadOnlyList<Action>> GetActiveAsync(UserId userId, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            return Task.FromResult<IReadOnlyList<Action>>(
                Actions.Where(action => action.UserId == userId && action.Status == ActionStatus.Active).ToList());
        }

        public Task<Action?> FindAsync(long actionId, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            return Task.FromResult(Actions.ElementAtOrDefault(checked((int)actionId - 1)));
        }

        public Task SaveAsync(CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            SaveCount++;
            return Task.CompletedTask;
        }
    }
}
