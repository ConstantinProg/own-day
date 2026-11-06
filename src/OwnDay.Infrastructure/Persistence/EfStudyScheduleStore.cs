using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfStudyScheduleStore(OwnDayDbContext db) : IStudyScheduleStore
{
    public Task<StudySchedule?> FindAsync(UserId user, CancellationToken token) =>
        db.Set<StudySchedule>().SingleOrDefaultAsync(s => s.UserId == user, token);

    public void Add(StudySchedule schedule) => db.Set<StudySchedule>().Add(schedule);
}
