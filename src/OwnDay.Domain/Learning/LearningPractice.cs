using System.Collections.Immutable;

namespace OwnDay.Domain.Learning;

public enum LearningPracticeStatus { Running, Paused, Completed, Stopped }
public sealed record LearningPractice(Guid Id, int RequestedCount, ImmutableArray<Guid> LessonIds,
    Guid? SkillId, LearningPracticeStatus Status, int Version = 1)
{
    public bool Supported => Version == 1 && Id != Guid.Empty && RequestedCount is >= 1 and <= 3 && !LessonIds.IsDefault &&
        LessonIds.Length <= RequestedCount && LessonIds.All(id => id != Guid.Empty) && LessonIds.Distinct().Count() == LessonIds.Length &&
        (LessonIds.Length == 0 ? SkillId is null : SkillId is not null && SkillId != Guid.Empty) && Enum.IsDefined(Status) &&
        (Status != LearningPracticeStatus.Completed || LessonIds.Length == RequestedCount);
    public bool Active => Status is LearningPracticeStatus.Running or LearningPracticeStatus.Paused;
}

public sealed partial class LearningContext
{
    public LearningPractice? Practice { get; private set; }

    public void BeginPractice(int count)
    {
        if (count is < 1 or > 3 || Practice is { Active: true } || Practice is { Supported: false })
            throw new InvalidOperationException("A bounded new practice is required.");
        Practice = new(Guid.NewGuid(), count, [], null, LearningPracticeStatus.Running);
    }

    public void IssuePracticeLesson(Lesson lesson, Lesson? previous)
    {
        if (Practice is not { Supported: true, Status: LearningPracticeStatus.Running } practice ||
            lesson.UserId != UserId || lesson.SkillId is null || lesson.Status is not (LessonStatus.AwaitingAnswer or LessonStatus.Paused) ||
            practice.LessonIds.Length >= practice.RequestedCount || practice.LessonIds.Contains(lesson.Id) ||
            practice.SkillId is not null && practice.SkillId != lesson.SkillId ||
            (practice.LessonIds.Length == 0 ? previous is not null : previous is null || previous.UserId != UserId ||
                previous.Id != practice.LessonIds[^1] || previous.Status != LessonStatus.Completed || previous.SkillId != lesson.SkillId))
            throw new InvalidOperationException("Practice requires the completed preceding exercise and the same owned skill.");
        Practice = practice with { LessonIds = practice.LessonIds.Add(lesson.Id), SkillId = lesson.SkillId,
            Status = lesson.Status == LessonStatus.Paused ? LearningPracticeStatus.Paused : LearningPracticeStatus.Running };
    }

    public void ReplaceRevealedPracticeLesson(Lesson previous, Lesson lesson)
    {
        if (Practice is not { Supported: true, Status: LearningPracticeStatus.Running, LessonIds.IsEmpty: false } practice ||
            previous.UserId != UserId || previous.Id != practice.LessonIds[^1] ||
            previous is not { Status: LessonStatus.Finished, Choice.SolutionSeen: true } ||
            lesson.UserId != UserId || lesson.Status != LessonStatus.AwaitingAnswer || lesson.SkillId != practice.SkillId ||
            practice.LessonIds.Contains(lesson.Id))
            throw new InvalidOperationException("Practice can only replace its last revealed exercise with a fresh check of the same skill.");
        Practice = practice with { LessonIds = practice.LessonIds.SetItem(practice.LessonIds.Length - 1, lesson.Id) };
    }

    public void PausePractice()
    {
        if (Practice is not { Supported: true, Active: true }) throw new InvalidOperationException("No active practice.");
        Practice = Practice with { Status = LearningPracticeStatus.Paused };
    }

    public void ResumePractice()
    {
        if (Practice is not { Supported: true, Active: true }) throw new InvalidOperationException("No active practice.");
        Practice = Practice with { Status = LearningPracticeStatus.Running };
    }

    public void StopPractice()
    {
        if (Practice is not { Supported: true, Active: true }) throw new InvalidOperationException("No active practice.");
        Practice = Practice with { Status = LearningPracticeStatus.Stopped };
    }

    public void CompletePractice(Lesson last)
    {
        if (Practice is not { Supported: true, Active: true } practice || practice.LessonIds.Length != practice.RequestedCount ||
            last.UserId != UserId || last.Id != practice.LessonIds[^1] || last.Status != LessonStatus.Completed)
            throw new InvalidOperationException("The requested exercises are not complete.");
        Practice = practice with { Status = LearningPracticeStatus.Completed };
    }
}
