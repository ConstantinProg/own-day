using OwnDay.Domain;
using OwnDay.Domain.Projects;
using OwnDay.Domain.SomedayMaybes;
using OwnDay.Domain.References;
using OwnDay.Domain.WaitingFors;

namespace OwnDay.Application.StructuredItems;

public interface IStructuredItemStore
{
    Task<bool> ProjectBelongsToAsync(long projectId, UserId userId, CancellationToken cancellationToken);
    Task<Project?> FindProjectAsync(UserId userId, long projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Project>> GetActiveProjectsAsync(UserId userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SomedayMaybe>> GetActiveSomedayMaybesAsync(UserId userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Reference>> GetActiveReferencesAsync(UserId userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WaitingFor>> GetActiveWaitingForsAsync(UserId userId, CancellationToken cancellationToken);
    Task<Project> AddAsync(Project project, CancellationToken cancellationToken);
    Task<SomedayMaybe> AddAsync(SomedayMaybe item, CancellationToken cancellationToken);
    Task<Reference> AddAsync(Reference item, CancellationToken cancellationToken);
    Task<WaitingFor> AddAsync(WaitingFor item, CancellationToken cancellationToken);
}
