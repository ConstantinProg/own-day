using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfLessonStore(OwnDayDbContext db) : ILessonStore
{
    public async Task<Lesson?> FindActiveAsync(UserId user, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var local = db.Set<Lesson>().Local.SingleOrDefault(lesson => lesson.UserId == user && db.Entry(lesson).State == EntityState.Added &&
            lesson.Status is LessonStatus.AwaitingAnswer or LessonStatus.Paused);
        if (local is not null) return local;
        var stored = await db.Set<Lesson>().Include(l => l.Interactions.OrderByDescending(i => i.Id).Take(10)).SingleOrDefaultAsync(l => l.UserId == user &&
            (l.Status == LessonStatus.AwaitingAnswer || l.Status == LessonStatus.Paused), token);
        return stored?.Status is LessonStatus.AwaitingAnswer or LessonStatus.Paused ? stored : null;
    }

    public void Add(Lesson lesson) => db.Set<Lesson>().Add(lesson);
}
