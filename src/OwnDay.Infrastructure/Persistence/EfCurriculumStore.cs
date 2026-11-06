using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfCurriculumStore(OwnDayDbContext db) : ICurriculumStore
{
    public Task<LearningProgram?> FindAsync(UserId user, CancellationToken token) =>
        db.Set<LearningProgram>().Include(p => p.Routes).SingleOrDefaultAsync(p => p.UserId == user &&
            db.Set<LearningContext>().Any(c => c.UserId == user && c.ProgramId == p.Id), token);

    public Task<LearningProgram?> FindAsync(UserId user, Guid programId, CancellationToken token) =>
        db.Set<LearningProgram>().Include(p => p.Routes).SingleOrDefaultAsync(p => p.UserId == user && p.Id == programId, token);

    public async Task<IReadOnlyList<LearningProgram>> ListAsync(UserId user, int offset, int limit, CancellationToken token) =>
        await db.Set<LearningProgram>().Include(p => p.Routes).Where(p => p.UserId == user)
            .OrderBy(p => p.Id).Skip(offset).Take(limit).ToListAsync(token);

    public async Task SelectAsync(LearningProgram program, CancellationToken token)
    {
        var context = await db.Set<LearningContext>().SingleOrDefaultAsync(c => c.UserId == program.UserId, token);
        token.ThrowIfCancellationRequested();
        if (context is null) db.Set<LearningContext>().Add(LearningContext.Create(program));
        else context.Select(program);
    }

    public async Task<IReadOnlySet<string>> CompletedTopicsAsync(UserId user, Guid programId, int version, CancellationToken token)
    {
        var program = await FindAsync(user, programId, token);
        if (program is null) return new HashSet<string>(StringComparer.Ordinal);
        var versions = program.ProgressVersions(version);
        return (await db.Set<Lesson>().Where(l => l.UserId == user && l.ProgramId == programId && l.RouteVersion != null &&
            versions.Contains(l.RouteVersion.Value) && l.Status == LessonStatus.Completed && l.IsCorrect == true)
            .Select(l => l.TopicCode!).ToListAsync(token)).ToHashSet(StringComparer.Ordinal);
    }

    public void Add(LearningProgram program) => db.Set<LearningProgram>().Add(program);
}
