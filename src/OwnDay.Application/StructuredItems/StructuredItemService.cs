using OwnDay.Domain;
using OwnDay.Domain.Projects;
using OwnDay.Domain.SomedayMaybes;
using OwnDay.Domain.References;
using OwnDay.Domain.WaitingFors;
using Action = OwnDay.Domain.Actions.Action;

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

    public async Task<Action> CreateActionAsync(UserId userId, string title, long? projectId, CancellationToken cancellationToken)
    {
        var action = Action.Create(userId, title, Now(), projectId);
        await ValidateProjectAsync(userId, projectId, cancellationToken);
        return await _store.AddAsync(action, cancellationToken);
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

    private async Task ValidateProjectAsync(UserId userId, long? projectId, CancellationToken cancellationToken)
    {
        if (projectId is long id && !await _store.ProjectBelongsToAsync(id, userId, cancellationToken))
        {
            throw new ArgumentException("Project was not found for this user.", nameof(projectId));
        }
    }
}
