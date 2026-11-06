using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public interface IKnowledgeStore
{
    Task<Skill?> FindAsync(UserId user, Guid skillId, CancellationToken token);
    Task<Skill?> FindTopicAsync(UserId user, Guid programId, int version, string topicCode, CancellationToken token);
    Task<IReadOnlyList<Skill>> ListAsync(UserId user, int offset, int limit, CancellationToken token);
    Task<IReadOnlyList<LearningEvidence>> EvidenceAsync(UserId user, Guid skillId, CancellationToken token);
    Task<ReviewSchedule?> ReviewAsync(UserId user, Guid skillId, CancellationToken token);
    Task<IReadOnlyList<ReviewItem>> DueReviewsAsync(UserId user, DateTimeOffset now, int offset, int limit, CancellationToken token);
    void Add(Skill skill);
    void Add(LearningEvidence evidence);
    void Add(ReviewSchedule schedule);
}

public sealed record SkillPage(IReadOnlyList<Skill> Skills, int Number, bool HasNext);

public sealed record ReviewItem(Skill Skill, ReviewSchedule Schedule);
public sealed record ReviewPage(IReadOnlyList<ReviewItem> Items, int Number, bool HasNext);
public sealed record SkillStatus(Skill Skill, LearnerSkillState State, IReadOnlyList<LearningEvidence> Evidence)
{
    public ReviewSchedule? Review { get; init; }
}

public sealed class KnowledgeService(IKnowledgeStore store, TimeProvider clock)
{
    public const int PageSize = 10;
    public const int MaxPageNumber = int.MaxValue / PageSize;

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

    public void RecordAnswer(Lesson lesson, string source, ReviewSchedule? schedule)
    {
        var evidence = LearningEvidence.FromAnswer(lesson, clock.GetUtcNow(), source);
        if (schedule is null) store.Add(ReviewSchedule.FromAnswer(evidence));
        else schedule.Record(evidence);
        store.Add(evidence);
    }

    public Task<ReviewSchedule?> ReviewAsync(UserId user, Guid skillId, CancellationToken token)
    {
        Validate(user, token);
        return store.ReviewAsync(user, skillId, token);
    }

    public bool IsDue(ReviewSchedule schedule) => schedule.IsDue(clock.GetUtcNow());

    public void Defer(ReviewSchedule schedule, bool skip) => schedule.Defer(clock.GetUtcNow(), skip);

    public async Task<ReviewPage> ReviewsAsync(UserId user, CancellationToken token, int page = 1)
    {
        Validate(user, token);
        if (page is < 1 or > MaxPageNumber) throw new ArgumentOutOfRangeException(nameof(page));
        var items = await store.DueReviewsAsync(user, clock.GetUtcNow(), (page - 1) * PageSize, PageSize + 1, token);
        return new(items.Take(PageSize).ToArray(), page, items.Count > PageSize);
    }

    public Task<Skill?> FindAsync(UserId user, Guid skillId, CancellationToken token)
    {
        Validate(user, token);
        return store.FindAsync(user, skillId, token);
    }

    public async Task<SkillStatus?> TopicStatusAsync(UserId user, Guid programId, int version, string code, CancellationToken token)
    {
        Validate(user, token);
        var skill = await store.FindTopicAsync(user, programId, version, code, token);
        return skill is null ? null : await StatusAsync(user, skill.Id, token);
    }

    public async Task<SkillPage> ListAsync(UserId user, CancellationToken token, int page = 1)
    {
        Validate(user, token);
        if (page is < 1 or > MaxPageNumber) throw new ArgumentOutOfRangeException(nameof(page));
        var skills = await store.ListAsync(user, (page - 1) * PageSize, PageSize + 1, token);
        return new(skills.Take(PageSize).ToArray(), page, skills.Count > PageSize);
    }

    public async Task<SkillStatus?> StatusAsync(UserId user, Guid skillId, CancellationToken token)
    {
        var skill = await FindAsync(user, skillId, token);
        if (skill is null) return null;
        var evidence = await store.EvidenceAsync(user, skillId, token);
        return new(skill, LearnerSkillState.Evaluate(evidence), evidence)
        {
            Review = await store.ReviewAsync(user, skillId, token)
        };
    }

    private static void Validate(UserId user, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!user.IsValid) throw new ArgumentException("Invalid user.", nameof(user));
    }
}
