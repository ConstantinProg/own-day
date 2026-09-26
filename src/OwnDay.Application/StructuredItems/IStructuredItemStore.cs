using OwnDay.Domain;
using OwnDay.Domain.Projects;
using OwnDay.Domain.SomedayMaybes;
using OwnDay.Domain.References;
using OwnDay.Domain.WaitingFors;
using Action = OwnDay.Domain.Actions.Action;

namespace OwnDay.Application.StructuredItems;

public interface IStructuredItemStore
{
    Task<bool> ProjectBelongsToAsync(long projectId, UserId userId, CancellationToken cancellationToken);
    Task<Project> AddAsync(Project project, CancellationToken cancellationToken);
    Task<Action> AddAsync(Action action, CancellationToken cancellationToken);
    Task<SomedayMaybe> AddAsync(SomedayMaybe item, CancellationToken cancellationToken);
    Task<Reference> AddAsync(Reference item, CancellationToken cancellationToken);
    Task<WaitingFor> AddAsync(WaitingFor item, CancellationToken cancellationToken);
}
