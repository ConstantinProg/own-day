using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public interface ILessonStore
{
    Task<Lesson?> FindActiveAsync(UserId user, CancellationToken token);
    void Add(Lesson lesson);
}

public sealed record LessonProgramContext(string Discipline, string Goal, string Experience,
    string? DiagnosticQuestion, string? DiagnosticAnswer);
public sealed record LessonRequest(string Topic, LessonProgramContext? Program = null, TeachingContext? Teaching = null,
    bool IsReview = false, SharedSkillContext? SharedSkill = null);
public sealed record SharedSkillContext(string Description, LearnerSkillState State);

public sealed record LessonMaterial(string Theory, string Question)
{
    public LessonProvenance? Provenance { get; init; }
    public string? ExpectedAnswer { get; init; }
}
public sealed record LessonAssessment(string Feedback, bool IsCorrect)
{
    public string Source { get; init; } = "LLM (unspecified model)";
}
public sealed record LessonExplanation(string Explanation);

public interface ILessonTeacher
{
    Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token);
    Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token);
    Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token);
}

public enum LessonProviderFailure
{
    Unavailable,
    HttpError,
    InvalidResponse,
    Timeout
}

public sealed class LessonProviderException(LessonProviderFailure reason = LessonProviderFailure.Unavailable) : Exception
{
    public LessonProviderFailure Reason { get; } = reason;
}

public abstract record LearningResult
{
    public sealed record Material(string Theory, string Question, string Topic) : LearningResult
    {
        public LessonProvenance? Provenance { get; init; }
        public bool HasReferenceCheck { get; init; }
    }
    public sealed record Assessment(string Feedback) : LearningResult
    {
        public AnswerAssessmentStatus Status { get; init; }
        public bool? DeterministicIsCorrect { get; init; }
    }
    public sealed record InvalidTopic : LearningResult;
    public sealed record InvalidAnswer : LearningResult;
    public sealed record NoActiveLesson : LearningResult;
    public sealed record ProviderUnavailable : LearningResult;
    public sealed record Session(string Topic, string Question, LessonStatus Status) : LearningResult
    {
        public LessonProvenance? Provenance { get; init; }
    }
    public sealed record Explanation(string Text, string Topic, string Question) : LearningResult;
    public sealed record InvalidClarification : LearningResult;
    public sealed record InvalidTransition : LearningResult;
    public sealed record Finished : LearningResult;
    public sealed record SkillNotFound : LearningResult;
    public sealed record ReviewNotDue : LearningResult;
    public sealed record ReviewDeferred(DateTimeOffset DueAt) : LearningResult;
}

public sealed class LearningService(ILessonStore store, ILessonTeacher teacher, KnowledgeService knowledge)
{
    public async Task<LearningResult> StartAsync(UserId user, string topic, CancellationToken token)
    {
        ValidateUser(user, token);
        topic = topic.Trim();
        if (string.IsNullOrWhiteSpace(topic) || topic.Length > 500)
        {
            return new LearningResult.InvalidTopic();
        }

        var active = await store.FindActiveAsync(user, token);
        if (active is not null)
        {
            return Session(active);
        }

        try
        {
            var material = await teacher.GenerateAsync(new LessonRequest(topic), token);
            token.ThrowIfCancellationRequested();
            var lesson = Lesson.Start(user, topic, material.Theory, material.Question, material.Provenance, material.ExpectedAnswer);
            await knowledge.AttachAsync(lesson, lesson.Topic, token);
            store.Add(lesson);
            return Material(lesson);
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new LearningResult.ProviderUnavailable();
        }
    }

    public async Task<LearningResult> AnswerAsync(UserId user, string answer, CancellationToken token)
    {
        ValidateUser(user, token);
        answer = answer.Trim();
        if (string.IsNullOrWhiteSpace(answer) || answer.Length > 3000)
        {
            return new LearningResult.InvalidAnswer();
        }

        var active = await store.FindActiveAsync(user, token);
        if (active is null)
        {
            return new LearningResult.NoActiveLesson();
        }
        if (active.Status != LessonStatus.AwaitingAnswer)
        {
            return new LearningResult.InvalidTransition();
        }

        LessonAssessment result;
        try
        {
            result = await teacher.AssessAsync(active, answer, token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result.Feedback) || result.Feedback.Length > 3000 ||
                string.IsNullOrWhiteSpace(result.Source) || result.Source.Length > 200)
                return new LearningResult.ProviderUnavailable();
            await knowledge.AttachAsync(active, active.Topic, token);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new LearningResult.ProviderUnavailable();
        }

        // State/evidence failures must escape so the caller rolls back the entire update transaction.
        active.Complete(answer, result.Feedback, result.IsCorrect);
        await knowledge.RecordAnswerAsync(active, result.Source, token);
        return new LearningResult.Assessment(result.Feedback)
        {
            Status = active.AssessmentStatus, DeterministicIsCorrect = active.DeterministicIsCorrect
        };
    }

    public async Task<LearningResult> PauseAsync(UserId user, CancellationToken token)
    {
        ValidateUser(user, token);
        var lesson = await store.FindActiveAsync(user, token);
        if (lesson is null) return new LearningResult.NoActiveLesson();
        if (lesson.Status != LessonStatus.AwaitingAnswer) return new LearningResult.InvalidTransition();
        lesson.Pause();
        return Session(lesson);
    }

    public Task<LearningResult> PracticeAsync(UserId user, Guid skillId, CancellationToken token) =>
        StartSkillLessonAsync(user, skillId, SkillLessonKind.Practice, token);

    public Task<LearningResult> ReviewAsync(UserId user, Guid skillId, CancellationToken token) =>
        StartSkillLessonAsync(user, skillId, SkillLessonKind.Review, token);

    public async Task<LearningResult> DeferReviewAsync(UserId user, Guid skillId, ReviewDeferral deferral, CancellationToken token)
    {
        ValidateUser(user, token);
        var schedule = await knowledge.ReviewAsync(user, skillId, token);
        if (schedule is null) return new LearningResult.SkillNotFound();
        var active = await store.FindActiveAsync(user, token);
        token.ThrowIfCancellationRequested();
        if (active?.SkillId == skillId) return new LearningResult.InvalidTransition();
        if (!knowledge.IsDue(schedule)) return new LearningResult.ReviewNotDue();
        knowledge.Defer(schedule, deferral);
        return new LearningResult.ReviewDeferred(schedule.DueAt);
    }

    private enum SkillLessonKind
    {
        Practice,
        Review
    }

    private async Task<LearningResult> StartSkillLessonAsync(UserId user, Guid skillId, SkillLessonKind kind, CancellationToken token)
    {
        ValidateUser(user, token);
        var skill = await knowledge.FindAsync(user, skillId, token);
        if (skill is null) return new LearningResult.SkillNotFound();
        var active = await store.FindActiveAsync(user, token);
        if (active is not null) return Session(active);
        if (kind == SkillLessonKind.Review)
        {
            var schedule = await knowledge.ReviewAsync(user, skillId, token);
            if (schedule is null || !knowledge.IsDue(schedule)) return new LearningResult.ReviewNotDue();
        }
        try
        {
            var material = await teacher.GenerateAsync(new LessonRequest(skill.Description, IsReview: kind == SkillLessonKind.Review), token);
            token.ThrowIfCancellationRequested();
            var lesson = Lesson.Start(user, skill.Title, material.Theory, material.Question, material.Provenance, material.ExpectedAnswer);
            lesson.LinkToSkill(skill);
            if (kind == SkillLessonKind.Review) lesson.MarkAsReview();
            store.Add(lesson);
            return Material(lesson);
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new LearningResult.ProviderUnavailable();
        }
    }

    public async Task<LearningResult> ResumeAsync(UserId user, CancellationToken token)
    {
        ValidateUser(user, token);
        var lesson = await store.FindActiveAsync(user, token);
        if (lesson is null) return new LearningResult.NoActiveLesson();
        if (lesson.Status == LessonStatus.Paused) lesson.Resume();
        return Session(lesson);
    }

    public async Task<LearningResult> FinishAsync(UserId user, CancellationToken token)
    {
        ValidateUser(user, token);
        var lesson = await store.FindActiveAsync(user, token);
        if (lesson is null) return new LearningResult.NoActiveLesson();
        lesson.Finish();
        return new LearningResult.Finished();
    }

    public Task<LearningResult> ClarifyAsync(UserId user, string request, CancellationToken token) =>
        ExplainAsync(user, request, LessonInteractionKind.Clarification, token);

    public Task<LearningResult> ExplainAsync(UserId user, CancellationToken token) =>
        ExplainAsync(user, "Explain the current material differently.", LessonInteractionKind.AlternativeExplanation, token);

    private async Task<LearningResult> ExplainAsync(UserId user, string request, LessonInteractionKind kind, CancellationToken token)
    {
        ValidateUser(user, token);
        request = request.Trim();
        if (string.IsNullOrWhiteSpace(request) || request.Length > 1000) return new LearningResult.InvalidClarification();
        var lesson = await store.FindActiveAsync(user, token);
        if (lesson is null) return new LearningResult.NoActiveLesson();
        if (lesson.Status != LessonStatus.AwaitingAnswer) return new LearningResult.InvalidTransition();
        try
        {
            var result = await teacher.ExplainAsync(lesson, request, kind, token);
            token.ThrowIfCancellationRequested();
            lesson.AddExplanation(kind, request, result.Explanation);
            return new LearningResult.Explanation(result.Explanation, lesson.Topic, lesson.Question);
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new LearningResult.ProviderUnavailable();
        }
    }

    internal static LearningResult.Material Material(Lesson lesson) => new(lesson.Theory, lesson.Question, lesson.Topic)
    {
        Provenance = lesson.Provenance, HasReferenceCheck = lesson.ExpectedAnswer is not null
    };

    private static LearningResult.Session Session(Lesson lesson) => new(lesson.Topic, lesson.Question, lesson.Status)
    {
        Provenance = lesson.Provenance
    };

    private static void ValidateUser(UserId user, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!user.IsValid)
        {
            throw new ArgumentException("Invalid user.", nameof(user));
        }
    }
}
