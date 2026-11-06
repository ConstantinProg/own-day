using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.UnitTests.Learning;

internal sealed class KnowledgeStore : IKnowledgeStore
{
    public List<LearningPreference> Preferences { get; } = [];
    public List<StrategyObservation> StrategyObservations { get; } = [];
    public Task<IReadOnlyList<LearningPreference>> PreferencesAsync(UserId user, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<LearningPreference>>(Preferences.Where(p => p.UserId == user).ToArray());
    public Task<IReadOnlyList<StrategyObservation>> StrategyObservationsAsync(UserId user, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<StrategyObservation>>(StrategyObservations);
    public void Add(LearningPreference preference) => Preferences.Add(preference);
    public void Remove(LearningPreference preference) => Preferences.Remove(preference);
    public Task<IReadOnlyDictionary<string, SkillStatus>> TopicStatusesAsync(LearningProgram program, CancellationToken token) =>
        Task.FromResult<IReadOnlyDictionary<string, SkillStatus>>(new Dictionary<string, SkillStatus>());

    public List<Skill> Skills { get; } = [];
    public List<LearningEvidence> Evidence { get; } = [];
    public List<ReviewSchedule> Reviews { get; } = [];
    public List<ProgramSkillLink> Links { get; } = [];
    public void Add(ProgramSkillLink link) => Links.Add(link);
    public Task<IReadOnlyList<ProgramSkillLink>> LinksAsync(UserId user, Guid programId, int progressVersion, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<ProgramSkillLink>>(Links.Where(l => l.UserId == user && l.ProgramId == programId && l.ProgressVersion == progressVersion).ToArray());
    public Task<Skill?> FindAsync(UserId user, Guid skillId, CancellationToken token) =>
        Task.FromResult(Skills.SingleOrDefault(s => s.UserId == user && s.Id == skillId));
    public Task<Skill?> FindTopicAsync(UserId user, Guid programId, int version, string topicCode, CancellationToken token) =>
        Task.FromResult<Skill?>(null);
    public Task<IReadOnlyList<Skill>> ListAsync(UserId user, int offset, int limit, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<Skill>>(Skills.Where(s => s.UserId == user).OrderBy(s => s.Title).ThenBy(s => s.Id).Skip(offset).Take(limit).ToArray());
    public Task<IReadOnlyList<LearningEvidence>> EvidenceAsync(UserId user, Guid skillId, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<LearningEvidence>>(Evidence.Where(e => e.UserId == user && e.SkillId == skillId).ToArray());
    public void Add(Skill skill) => Skills.Add(skill);
    public void Add(LearningEvidence evidence) => Evidence.Add(evidence);
    public void Add(ReviewSchedule schedule) => Reviews.Add(schedule);
    public Task<ReviewSchedule?> ReviewAsync(UserId user, Guid skillId, CancellationToken token) =>
        Task.FromResult(Reviews.SingleOrDefault(r => r.UserId == user && r.SkillId == skillId));
    public Task<IReadOnlyList<ReviewItem>> DueReviewsAsync(UserId user, DateTimeOffset now, int offset, int limit, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<ReviewItem>>(Reviews.Where(r => r.UserId == user && r.IsDue(now))
            .OrderBy(r => r.DueAt).ThenBy(r => r.SkillId).Skip(offset).Take(limit)
            .Select(r => new ReviewItem(Skills.Single(s => s.Id == r.SkillId), r)).ToArray());
}
