using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public interface ILessonStore
{
    Task<Lesson?> FindActiveAsync(UserId user, CancellationToken token);
    void Add(Lesson lesson);
}

public sealed record LessonMaterial(string Theory, string Question);
public sealed record LessonAssessment(string Feedback, bool IsCorrect);

public interface ILessonTeacher
{
    Task<LessonMaterial> GenerateAsync(string topic, CancellationToken token);
    Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token);
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
    public sealed record Material(string Theory, string Question) : LearningResult;
    public sealed record Assessment(string Feedback) : LearningResult;
    public sealed record InvalidTopic : LearningResult;
    public sealed record InvalidAnswer : LearningResult;
    public sealed record NoActiveLesson : LearningResult;
    public sealed record ProviderUnavailable : LearningResult;
}

public sealed class LearningService(ILessonStore store, ILessonTeacher teacher)
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
            return new LearningResult.Material(active.Theory, active.Question);
        }

        try
        {
            var material = await teacher.GenerateAsync(topic, token);
            var lesson = Lesson.Start(user, topic, material.Theory, material.Question);
            store.Add(lesson);
            return new LearningResult.Material(lesson.Theory, lesson.Question);
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

        try
        {
            var result = await teacher.AssessAsync(active, answer, token);
            active.Complete(answer, result.Feedback, result.IsCorrect);
            return new LearningResult.Assessment(result.Feedback);
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new LearningResult.ProviderUnavailable();
        }
    }

    private static void ValidateUser(UserId user, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!user.IsValid)
        {
            throw new ArgumentException("Invalid user.", nameof(user));
        }
    }
}
