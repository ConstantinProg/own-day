using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfKnowledgeStore(OwnDayDbContext db) : IKnowledgeStore
{
    public Task<Skill?> FindAsync(UserId user, Guid skillId, CancellationToken token) =>
        db.Set<Skill>().SingleOrDefaultAsync(s => s.UserId == user && s.Id == skillId, token);

    public async Task<Skill?> FindTopicAsync(UserId user, Guid programId, int version, string topicCode, CancellationToken token)
    {
        var program = await db.Set<LearningProgram>().Include(p => p.Routes)
            .SingleOrDefaultAsync(p => p.UserId == user && p.Id == programId, token);
        if (program is null) return null;
        var versions = program.ProgressVersions(version);
        var root = program.Routes.Single(r => r.Version == version);
        var progressVersion = root.ProgressVersion ?? root.Version;
        var linked = await db.Set<ProgramSkillLink>().Where(l => l.UserId == user && l.ProgramId == programId &&
            l.ProgressVersion == progressVersion && l.TopicCode == topicCode).Select(l => (Guid?)l.SkillId).SingleOrDefaultAsync(token);
        if (linked is Guid skillId) return await FindAsync(user, skillId, token);
        return await db.Set<Skill>().FirstOrDefaultAsync(s => s.UserId == user && db.Set<Lesson>().Any(l =>
            l.UserId == user && l.ProgramId == programId && l.RouteVersion != null && versions.Contains(l.RouteVersion.Value) &&
            l.TopicCode == topicCode && l.SkillId == s.Id), token);
    }

    public async Task<IReadOnlyList<Skill>> ListAsync(UserId user, int offset, int limit, CancellationToken token) =>
        await db.Set<Skill>().Where(s => s.UserId == user).OrderBy(s => s.Title).ThenBy(s => s.Id).Skip(offset).Take(limit).ToListAsync(token);

    public async Task<IReadOnlyList<LearningEvidence>> EvidenceAsync(UserId user, Guid skillId, CancellationToken token)
    {
        var evidence = await db.Set<LearningEvidence>().Where(e => e.UserId == user && e.SkillId == skillId).ToListAsync(token);
        return evidence.OrderBy(e => e.ObservedAt).ThenBy(e => e.LessonId).ToArray();
    }

    public void Add(Skill skill) => db.Set<Skill>().Add(skill);
    public void Add(LearningEvidence evidence) => db.Set<LearningEvidence>().Add(evidence);
    public void Add(ReviewSchedule schedule) => db.Set<ReviewSchedule>().Add(schedule);
    public void Add(ProgramSkillLink link) => db.Set<ProgramSkillLink>().Add(link);

    public async Task<IReadOnlyList<ProgramSkillLink>> LinksAsync(UserId user, Guid programId, int progressVersion, CancellationToken token) =>
        await db.Set<ProgramSkillLink>().Where(l => l.UserId == user && l.ProgramId == programId && l.ProgressVersion == progressVersion)
            .OrderBy(l => l.TopicCode).ToListAsync(token);

    public Task<ReviewSchedule?> ReviewAsync(UserId user, Guid skillId, CancellationToken token) =>
        db.Set<ReviewSchedule>().SingleOrDefaultAsync(r => r.UserId == user && r.SkillId == skillId, token);

    public async Task<IReadOnlyList<ReviewItem>> DueReviewsAsync(UserId user, DateTimeOffset now, int offset, int limit,
        CancellationToken token)
    {
        return await (from review in db.Set<ReviewSchedule>()
                      join skill in db.Set<Skill>() on new { review.SkillId, review.UserId }
                          equals new { SkillId = skill.Id, skill.UserId }
                      where review.UserId == user && review.DueAt <= now
                      orderby review.DueAt, review.SkillId
                      select new ReviewItem(skill, review)).Skip(offset).Take(limit).ToListAsync(token);
    }
}
