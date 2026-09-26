using Microsoft.EntityFrameworkCore;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Projects;
using OwnDay.Domain.SomedayMaybes;
using OwnDay.Domain.References;
using OwnDay.Domain.WaitingFors;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfStructuredItemStore(OwnDayDbContext dbContext) : IStructuredItemStore
{
    public Task<bool> ProjectBelongsToAsync(long projectId, UserId userId, CancellationToken cancellationToken) =>
        dbContext.Projects.AnyAsync(project => project.Id == projectId && project.UserId == userId, cancellationToken);

    public Task<Project?> FindProjectAsync(UserId userId, long projectId, CancellationToken cancellationToken) =>
        dbContext.Projects.AsNoTracking().SingleOrDefaultAsync(project => project.UserId == userId && project.Id == projectId, cancellationToken);

    public async Task<IReadOnlyList<Project>> GetActiveProjectsAsync(UserId userId, CancellationToken cancellationToken) =>
        await dbContext.Projects.AsNoTracking()
            .Where(project => project.UserId == userId && project.Status == ProjectStatus.Active)
            .OrderBy(project => project.CreatedAt).ThenBy(project => project.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SomedayMaybe>> GetActiveSomedayMaybesAsync(UserId userId, CancellationToken cancellationToken) =>
        await dbContext.SomedayMaybes.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == SomedayMaybeStatus.Active)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Reference>> GetActiveReferencesAsync(UserId userId, CancellationToken cancellationToken) =>
        await dbContext.References.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == ReferenceStatus.Active)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WaitingFor>> GetActiveWaitingForsAsync(UserId userId, CancellationToken cancellationToken) =>
        await dbContext.WaitingFors.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == WaitingForStatus.Active)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

    public async Task<Project> AddAsync(Project project, CancellationToken cancellationToken)
    {
        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync(cancellationToken);
        return project;
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
