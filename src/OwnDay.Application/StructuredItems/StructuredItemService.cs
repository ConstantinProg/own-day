using OwnDay.Domain;
using OwnDay.Domain.Projects;
using OwnDay.Domain.SomedayMaybes;
using OwnDay.Domain.References;
using OwnDay.Domain.WaitingFors;

namespace OwnDay.Application.StructuredItems;

public sealed class StructuredItemService
{
    private readonly IStructuredItemStore _store;
    private readonly TimeProvider _timeProvider;

    public StructuredItemService(IStructuredItemStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public Task<Project> CreateProjectAsync(UserId userId, string title, CancellationToken cancellationToken) =>
        _store.AddAsync(Project.Create(userId, title, Now()), cancellationToken);

    public Task<Project?> FindProjectAsync(UserId userId, long projectId, CancellationToken cancellationToken)
    {
        RequireOwner(userId);
        return projectId > 0
            ? _store.FindProjectAsync(userId, projectId, cancellationToken)
            : Task.FromResult<Project?>(null);
    }

    public Task<IReadOnlyList<Project>> GetActiveProjectsAsync(UserId userId, CancellationToken cancellationToken)
    {
        RequireOwner(userId);
        return _store.GetActiveProjectsAsync(userId, cancellationToken);
    }

    public Task<IReadOnlyList<SomedayMaybe>> GetActiveSomedayMaybesAsync(UserId userId, CancellationToken cancellationToken)
    {
        RequireOwner(userId);
        return _store.GetActiveSomedayMaybesAsync(userId, cancellationToken);
    }

    public Task<IReadOnlyList<Reference>> GetActiveReferencesAsync(UserId userId, CancellationToken cancellationToken)
    {
        RequireOwner(userId);
        return _store.GetActiveReferencesAsync(userId, cancellationToken);
    }

    public Task<IReadOnlyList<WaitingFor>> GetActiveWaitingForsAsync(UserId userId, CancellationToken cancellationToken)
    {
        RequireOwner(userId);
        return _store.GetActiveWaitingForsAsync(userId, cancellationToken);
    }

    public async Task<SomedayMaybe> CreateSomedayMaybeAsync(UserId userId, string text, long? projectId, CancellationToken cancellationToken)
    {
        var item = SomedayMaybe.Create(userId, text, Now(), projectId);
        await ValidateProjectAsync(userId, projectId, cancellationToken);
        return await _store.AddAsync(item, cancellationToken);
    }

    public async Task<Reference> CreateReferenceAsync(UserId userId, string text, long? projectId, CancellationToken cancellationToken)
    {
        var item = Reference.Create(userId, text, Now(), projectId);
        await ValidateProjectAsync(userId, projectId, cancellationToken);
        return await _store.AddAsync(item, cancellationToken);
    }

    public async Task<WaitingFor> CreateWaitingForAsync(UserId userId, string description, string? source, long? projectId, CancellationToken cancellationToken)
    {
        var item = WaitingFor.Create(userId, description, source, Now(), projectId);
        await ValidateProjectAsync(userId, projectId, cancellationToken);
        return await _store.AddAsync(item, cancellationToken);
    }

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    private static void RequireOwner(UserId userId) => ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId.Value);

    private async Task ValidateProjectAsync(UserId userId, long? projectId, CancellationToken cancellationToken)
    {
        if (projectId is long id && !await _store.ProjectBelongsToAsync(id, userId, cancellationToken))
        {
            throw new ArgumentException("Project was not found for this user.", nameof(projectId));
        }
    }
}
