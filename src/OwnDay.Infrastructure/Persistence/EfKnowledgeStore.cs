using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfKnowledgeStore(OwnDayDbContext db) : IKnowledgeStore
{
    public async Task<IReadOnlyList<LearningPreference>> PreferencesAsync(UserId user, CancellationToken token)
    {
        await db.Set<LearningPreference>().Where(p => p.UserId == user).LoadAsync(token);
        // The command reply is built before the surrounding update transaction commits.
        return db.Set<LearningPreference>().Local.Where(p => p.UserId == user).OrderBy(p => p.Kind).ToArray();
    }

    public void Add(LearningPreference preference) => db.Set<LearningPreference>().Add(preference);
    public void Remove(LearningPreference preference) => db.Set<LearningPreference>().Remove(preference);

    public async Task<IReadOnlyList<StrategyObservation>> StrategyObservationsAsync(UserId user, CancellationToken token)
    {
        // One row per assessed lesson; old lessons without a teaching snapshot are not reclassified.
        var recent = await (from evidence in db.Set<LearningEvidence>()
                            join lesson in db.Set<Lesson>() on new { evidence.LessonId, evidence.UserId }
                                equals new { LessonId = lesson.Id, lesson.UserId }
                            where evidence.UserId == user && lesson.Teaching != null
                            orderby evidence.ObservedAt descending, evidence.LessonId
                            select new { evidence.LessonId, lesson.Teaching, evidence.AssessedCorrect, evidence.AssessmentStatus })
            .Take(100).ToListAsync(token);
        return recent.Select(e => new StrategyObservation(e.LessonId, e.Teaching!.Strategy,
            e.AssessedCorrect, e.AssessmentStatus == AnswerAssessmentStatus.Conflict)).ToArray();
    }

    public async Task<IReadOnlyDictionary<string, SkillStatus>> TopicStatusesAsync(LearningProgram program, CancellationToken token)
    {
        var user = program.UserId;
        var versions = program.ProgressVersions(program.CurrentRoute.Version);
        var links = await LinksAsync(user, program.Id, program.CurrentRoute.ProgressRootVersion, token);
        var lessonSkills = await db.Set<Lesson>().Where(l => l.UserId == user && l.ProgramId == program.Id &&
            l.RouteVersion != null && versions.Contains(l.RouteVersion.Value) && l.SkillId != null)
            .Select(l => new { l.TopicCode, l.SkillId }).Distinct().ToListAsync(token);
        var topicSkills = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var topic in program.CurrentRoute.Topics)
        {
            var skillId = links.SingleOrDefault(l => l.TopicCode == topic.Code)?.SkillId
                ?? lessonSkills.FirstOrDefault(l => l.TopicCode == topic.Code)?.SkillId;
            if (skillId is Guid id) topicSkills.Add(topic.Code, id);
        }
        if (topicSkills.Count == 0) return new Dictionary<string, SkillStatus>();
        var ids = topicSkills.Values.Distinct().ToArray();
        var skills = await db.Set<Skill>().Where(s => s.UserId == user && ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, token);
        var evidence = await db.Set<LearningEvidence>().Where(e => e.UserId == user && ids.Contains(e.SkillId)).ToListAsync(token);
        var reviews = await db.Set<ReviewSchedule>().Where(r => r.UserId == user && ids.Contains(r.SkillId)).ToDictionaryAsync(r => r.SkillId, token);
        var statuses = new Dictionary<string, SkillStatus>(StringComparer.Ordinal);
        foreach (var (code, id) in topicSkills)
        {
            if (!skills.TryGetValue(id, out var skill)) continue;
            var observations = evidence.Where(e => e.SkillId == id).OrderBy(e => e.ObservedAt).ThenBy(e => e.LessonId).ToArray();
            statuses.Add(code, new(skill, LearnerSkillState.Evaluate(observations), observations)
            {
                Review = reviews.GetValueOrDefault(id)
            });
        }
        return statuses;
    }

    public Task<Skill?> FindAsync(UserId user, Guid skillId, CancellationToken token) =>
        db.Set<Skill>().SingleOrDefaultAsync(s => s.UserId == user && s.Id == skillId, token);

    public async Task<Skill?> FindTopicAsync(UserId user, Guid programId, int version, string topicCode, CancellationToken token)
    {
        var program = await db.Set<LearningProgram>().Include(p => p.Routes)
            .SingleOrDefaultAsync(p => p.UserId == user && p.Id == programId, token);
        if (program is null) return null;
        var versions = program.ProgressVersions(version);
        var progressVersion = program.Routes.Single(r => r.Version == version).ProgressRootVersion;
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
