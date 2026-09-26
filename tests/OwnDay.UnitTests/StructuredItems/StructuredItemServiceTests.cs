using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Projects;
using OwnDay.Domain.SomedayMaybes;
using OwnDay.Domain.References;
using OwnDay.Domain.WaitingFors;
using Xunit;

namespace OwnDay.UnitTests.StructuredItems;

public sealed class StructuredItemServiceTests
{
    private static readonly UserId Alice = new(1);
    private static readonly UserId Bob = new(2);

    [Fact]
    public async Task CreateProject_ValidInput_PersistsOwnedProjectAndPropagatesCancellation()
    {
        var store = new RecordingStore();
        var service = new StructuredItemService(store, TimeProvider.System);
        using var source = new CancellationTokenSource();

        var project = await service.CreateProjectAsync(Alice, "Outcome", source.Token);

        Assert.Equal(Alice, project.UserId);
        Assert.Equal(ProjectStatus.Active, project.Status);
        Assert.Same(project, store.LastAdded);
        Assert.Equal(source.Token, store.LastToken);
    }

    [Fact]
    public async Task CreateAll_WithoutProject_PersistsOwnedRecords()
    {
        var store = new RecordingStore();
        var service = new StructuredItemService(store, TimeProvider.System);
        var token = CancellationToken.None;

        Assert.Null((await service.CreateSomedayMaybeAsync(Alice, "Idea", null, token)).ProjectId);
        Assert.Null((await service.CreateReferenceAsync(Alice, "Note", null, token)).ProjectId);
        Assert.Null((await service.CreateWaitingForAsync(Alice, "Reply", null, null, token)).ProjectId);
        Assert.Equal(3, store.AddCount);
        Assert.Equal(0, store.ProjectLookupCount);
    }

    [Fact]
    public async Task CreateAll_WithOwnedProject_PersistsRelationsAndPropagatesCancellation()
    {
        var store = new RecordingStore { OwnedProjectId = 7, ProjectOwner = Alice };
        var service = new StructuredItemService(store, TimeProvider.System);
        using var source = new CancellationTokenSource();

        Assert.Equal(7, (await service.CreateSomedayMaybeAsync(Alice, "Idea", 7, source.Token)).ProjectId);
        Assert.Equal(7, (await service.CreateReferenceAsync(Alice, "Note", 7, source.Token)).ProjectId);
        Assert.Equal(7, (await service.CreateWaitingForAsync(Alice, "Reply", null, 7, source.Token)).ProjectId);
        Assert.Equal(3, store.ProjectLookupCount);
        Assert.Equal(3, store.AddCount);
        Assert.Equal(source.Token, store.LastToken);
    }

    [Theory]
    [InlineData(7, 1)]
    [InlineData(8, 2)]
    public async Task Create_WithForeignOrUnknownProject_RejectsBeforeInsert(long projectId, long owner)
    {
        var store = new RecordingStore { OwnedProjectId = 7, ProjectOwner = Bob };
        var service = new StructuredItemService(store, TimeProvider.System);
        var user = new UserId(owner);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateSomedayMaybeAsync(user, "Idea", projectId, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateReferenceAsync(user, "Note", projectId, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateWaitingForAsync(user, "Reply", null, projectId, CancellationToken.None));
        Assert.Equal(0, store.AddCount);
    }

    private sealed class RecordingStore : IStructuredItemStore
    {
        public long? OwnedProjectId { get; init; }
        public UserId ProjectOwner { get; init; }
        public object? LastAdded { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public int AddCount { get; private set; }
        public int ProjectLookupCount { get; private set; }

        public Task<bool> ProjectBelongsToAsync(long projectId, UserId userId, CancellationToken cancellationToken)
        {
            ProjectLookupCount++;
            LastToken = cancellationToken;
            return Task.FromResult(OwnedProjectId == projectId && ProjectOwner == userId);
        }

        public Task<Project?> FindProjectAsync(UserId userId, long projectId, CancellationToken cancellationToken) =>
            Task.FromResult<Project?>(null);

        public Task<IReadOnlyList<Project>> GetActiveProjectsAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Project>>([]);

        public Task<IReadOnlyList<SomedayMaybe>> GetActiveSomedayMaybesAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SomedayMaybe>>([]);

        public Task<IReadOnlyList<Reference>> GetActiveReferencesAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reference>>([]);

        public Task<IReadOnlyList<WaitingFor>> GetActiveWaitingForsAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WaitingFor>>([]);

        public Task<Project> AddAsync(Project project, CancellationToken cancellationToken) => Add(project, cancellationToken);
        public Task<SomedayMaybe> AddAsync(SomedayMaybe item, CancellationToken cancellationToken) => Add(item, cancellationToken);
        public Task<Reference> AddAsync(Reference item, CancellationToken cancellationToken) => Add(item, cancellationToken);
        public Task<WaitingFor> AddAsync(WaitingFor item, CancellationToken cancellationToken) => Add(item, cancellationToken);

        private Task<T> Add<T>(T item, CancellationToken cancellationToken)
        {
            LastAdded = item;
            LastToken = cancellationToken;
            AddCount++;
            return Task.FromResult(item);
        }
    }
}
