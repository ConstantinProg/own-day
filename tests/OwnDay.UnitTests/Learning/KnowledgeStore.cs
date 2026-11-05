using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.UnitTests.Learning;

internal sealed class KnowledgeStore : IKnowledgeStore
{
    public List<Skill> Skills { get; } = [];
    public List<LearningEvidence> Evidence { get; } = [];
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
}
