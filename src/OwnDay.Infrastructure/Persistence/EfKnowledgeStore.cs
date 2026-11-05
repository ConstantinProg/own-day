using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfKnowledgeStore(OwnDayDbContext db) : IKnowledgeStore
{
    public Task<Skill?> FindAsync(UserId user, Guid skillId, CancellationToken token) =>
        db.Set<Skill>().SingleOrDefaultAsync(s => s.UserId == user && s.Id == skillId, token);

    public Task<Skill?> FindTopicAsync(UserId user, Guid programId, int version, string topicCode, CancellationToken token) =>
        db.Set<Skill>().FirstOrDefaultAsync(s => s.UserId == user && db.Set<Lesson>().Any(l =>
            l.UserId == user && l.ProgramId == programId && l.RouteVersion == version && l.TopicCode == topicCode && l.SkillId == s.Id), token);

    public async Task<IReadOnlyList<Skill>> ListAsync(UserId user, CancellationToken token) =>
        await db.Set<Skill>().Where(s => s.UserId == user).OrderBy(s => s.Title).ThenBy(s => s.Id).ToListAsync(token);

    public async Task<IReadOnlyList<LearningEvidence>> EvidenceAsync(UserId user, Guid skillId, CancellationToken token)
    {
        var evidence = await db.Set<LearningEvidence>().Where(e => e.UserId == user && e.SkillId == skillId).ToListAsync(token);
        return evidence.OrderBy(e => e.ObservedAt).ThenBy(e => e.LessonId).ToArray();
    }

    public void Add(Skill skill) => db.Set<Skill>().Add(skill);
    public void Add(LearningEvidence evidence) => db.Set<LearningEvidence>().Add(evidence);
}
