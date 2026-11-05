using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public interface IKnowledgeStore
{
    Task<Skill?> FindAsync(UserId user, Guid skillId, CancellationToken token);
    Task<Skill?> FindTopicAsync(UserId user, Guid programId, int version, string topicCode, CancellationToken token);
    Task<IReadOnlyList<Skill>> ListAsync(UserId user, CancellationToken token);
    Task<IReadOnlyList<LearningEvidence>> EvidenceAsync(UserId user, Guid skillId, CancellationToken token);
    void Add(Skill skill);
    void Add(LearningEvidence evidence);
}

public sealed record SkillStatus(Skill Skill, LearnerSkillState State, IReadOnlyList<LearningEvidence> Evidence);

public sealed class KnowledgeService(IKnowledgeStore store, TimeProvider clock)
{
    public async Task AttachAsync(Lesson lesson, string description, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (lesson.SkillId is not null) return;
        var skill = lesson.ProgramId is Guid programId
            ? await store.FindTopicAsync(lesson.UserId, programId, lesson.RouteVersion!.Value, lesson.TopicCode!, token)
            : null;
        token.ThrowIfCancellationRequested();
        if (skill is null)
        {
            skill = Skill.Create(lesson.UserId, lesson.Topic, description);
            store.Add(skill);
        }
        lesson.LinkToSkill(skill);
    }

    public void RecordAnswer(Lesson lesson, string source) =>
        store.Add(LearningEvidence.FromAnswer(lesson, clock.GetUtcNow(), source));

    public Task<Skill?> FindAsync(UserId user, Guid skillId, CancellationToken token)
    {
        Validate(user, token);
        return store.FindAsync(user, skillId, token);
    }

    public Task<IReadOnlyList<Skill>> ListAsync(UserId user, CancellationToken token)
    {
        Validate(user, token);
        return store.ListAsync(user, token);
    }

    public async Task<SkillStatus?> StatusAsync(UserId user, Guid skillId, CancellationToken token)
    {
        var skill = await FindAsync(user, skillId, token);
        if (skill is null) return null;
        var evidence = await store.EvidenceAsync(user, skillId, token);
        return new(skill, LearnerSkillState.Evaluate(evidence), evidence);
    }

    private static void Validate(UserId user, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!user.IsValid) throw new ArgumentException("Invalid user.", nameof(user));
    }
}
