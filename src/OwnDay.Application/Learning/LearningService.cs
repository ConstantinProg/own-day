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

public sealed class LessonProviderException : Exception;

public sealed class LearningService(ILessonStore store, ILessonTeacher teacher)
{
    public async Task<IReadOnlyList<string>> HandleAsync(UserId user, bool start, string input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!user.IsValid) throw new ArgumentException("Invalid user.", nameof(user));
        input = input.Trim();
        if (string.IsNullOrWhiteSpace(input) || input.Length > (start ? 500 : 3000))
            return [start ? "Use /learn <topic> (up to 500 characters)." : "Use /answer <answer> (up to 3000 characters)."];
        var active = await store.FindActiveAsync(user, token);
        if (start && active is not null)
            return [active.Theory + "\n\n" + active.Question + "\n\n/answer <answer>"];
        if (!start && active is null) return ["Start a lesson with /learn <topic>."];
        try
        {
            if (start)
            {
                var material = await teacher.GenerateAsync(input, token);
                var lesson = Lesson.Start(user, input, material.Theory, material.Question);
                store.Add(lesson);
                return [lesson.Theory + "\n\n" + lesson.Question + "\n\n/answer <answer>"];
            }
            var result = await teacher.AssessAsync(active!, input, token);
            active!.Complete(input, result.Feedback, result.IsCorrect);
            return ["AI assessment (may be inaccurate):\n" + result.Feedback + "\n\n/learn <topic>"];
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return ["The teacher is temporarily unavailable or returned invalid material. Retry the command."];
        }
    }
}
