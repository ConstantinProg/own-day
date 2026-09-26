using Microsoft.EntityFrameworkCore;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Projects;
using OwnDay.Domain.SomedayMaybes;
using OwnDay.Domain.References;
using OwnDay.Domain.WaitingFors;
using Action = OwnDay.Domain.Actions.Action;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfStructuredItemStore(OwnDayDbContext dbContext) : IStructuredItemStore
{
    public Task<bool> ProjectBelongsToAsync(long projectId, UserId userId, CancellationToken cancellationToken) =>
        dbContext.Projects.AnyAsync(project => project.Id == projectId && project.UserId == userId, cancellationToken);

    public async Task<Project> AddAsync(Project project, CancellationToken cancellationToken)
    {
        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync(cancellationToken);
        return project;
    }

    public async Task<Action> AddAsync(Action action, CancellationToken cancellationToken)
    {
        dbContext.Actions.Add(action);
        await dbContext.SaveChangesAsync(cancellationToken);
        return action;
    }

    public async Task<SomedayMaybe> AddAsync(SomedayMaybe item, CancellationToken cancellationToken)
    {
        dbContext.SomedayMaybes.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task<Reference> AddAsync(Reference item, CancellationToken cancellationToken)
    {
        dbContext.References.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task<WaitingFor> AddAsync(WaitingFor item, CancellationToken cancellationToken)
    {
        dbContext.WaitingFors.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        return item;
    }
}
