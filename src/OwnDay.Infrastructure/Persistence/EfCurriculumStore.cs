using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfCurriculumStore(OwnDayDbContext db) : ICurriculumStore
{
    public Task<LearningProgram?> FindAsync(UserId user, CancellationToken token) =>
        db.Set<LearningProgram>().Include(p => p.Routes).SingleOrDefaultAsync(p => p.UserId == user, token);

    public async Task<IReadOnlySet<string>> CompletedTopicsAsync(UserId user, Guid programId, int version, CancellationToken token) =>
        (await db.Set<Lesson>().Where(l => l.UserId == user && l.ProgramId == programId && l.RouteVersion == version &&
            l.Status == LessonStatus.Completed && l.IsCorrect == true).Select(l => l.TopicCode!).ToListAsync(token)).ToHashSet(StringComparer.Ordinal);

    public void Add(LearningProgram program) => db.Set<LearningProgram>().Add(program);
}
