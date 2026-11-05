using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfLessonStore(OwnDayDbContext db) : ILessonStore
{
    public Task<Lesson?> FindActiveAsync(UserId user, CancellationToken token) =>
        db.Set<Lesson>().SingleOrDefaultAsync(l => l.UserId == user && l.Answer == null, token);

    public void Add(Lesson lesson) => db.Set<Lesson>().Add(lesson);
}
