using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Dialogue;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfLearningSessionStore(OwnDayDbContext db) : ILearningSessionStore
{
    public Task<LearningSession?> FindLatestAsync(UserId user, CancellationToken token) =>
        db.Set<LearningSession>().Where(session => session.UserId == user)
            .OrderByDescending(session => session.StartedAt).ThenByDescending(session => session.FirstSourceOrder)
            .FirstOrDefaultAsync(token);

    public void Add(LearningSession session) => db.Add(session);
}
