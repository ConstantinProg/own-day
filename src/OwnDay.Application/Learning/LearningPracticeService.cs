using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public enum PracticeReplyKind { Unavailable, NeedProgram, NeedDiagnosis, Current, ReadyNext, Paused, Stopped, Completed, ProviderUnavailable }
public sealed record LearningPracticeResult(PracticeReplyKind Kind, LearningPractice? Practice = null, LearningResult? Lesson = null);

public sealed class LearningPracticeService(ICurriculumStore programs, ILessonStore lessons, LearningService learning, CurriculumService curriculum)
{
    public async Task<LearningPracticeResult> ExecuteAsync(UserId user, DialogueOperationKind operation, string arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var context = await programs.FindContextAsync(user, token);
        if (context is null) return new(PracticeReplyKind.NeedProgram);
        if (context.Practice is { Supported: false }) return new(PracticeReplyKind.Unavailable);
        var active = await lessons.FindActiveAsync(user, token);
        if (operation == DialogueOperationKind.BeginPractice)
        {
            if (!int.TryParse(arguments, out var count) || count is < 1 or > 3) return new(PracticeReplyKind.Unavailable);
            var program = await programs.FindAsync(user, token);
            if (program is null) return new(PracticeReplyKind.NeedProgram);
            if (program.DiagnosticPending) return new(PracticeReplyKind.NeedDiagnosis);
            if (context.Practice is not { Active: true })
            {
                context.BeginPractice(count);
                if (active is not null) context.IssuePracticeLesson(active, null);
            }
        }
        if (context.Practice is not { Supported: true } practice) return new(PracticeReplyKind.Unavailable);
        if (!practice.Active) return new(practice.Status == LearningPracticeStatus.Completed ? PracticeReplyKind.Completed : PracticeReplyKind.Stopped, practice);
        if (operation == DialogueOperationKind.PracticeInfo)
            return new(practice.Status == LearningPracticeStatus.Paused ? PracticeReplyKind.Paused : PracticeReplyKind.ReadyNext, practice);
        if (operation is DialogueOperationKind.PausePractice or DialogueOperationKind.StopPractice)
        {
            if (active?.Status == LessonStatus.AwaitingAnswer) await learning.PauseAsync(user, token);
            if (operation == DialogueOperationKind.StopPractice) context.StopPractice();
            else context.PausePractice();
            return new(operation == DialogueOperationKind.StopPractice ? PracticeReplyKind.Stopped : PracticeReplyKind.Paused, context.Practice);
        }
        if (operation == DialogueOperationKind.ResumePractice) context.ResumePractice();
        if (context.Practice!.Status == LearningPracticeStatus.Paused && operation != DialogueOperationKind.ResumePractice)
            return new(PracticeReplyKind.Paused, context.Practice);
        if (active is not null)
        {
            if (practice.LessonIds.Length > 0 && practice.LessonIds[^1] != active.Id)
                return new(PracticeReplyKind.Unavailable, context.Practice, LearningService.Session(active));
            if (active is { AcceptsAnswer: true, Choice.SolutionSeen: true } &&
                operation == DialogueOperationKind.NextPractice && !practice.LessonIds.IsEmpty)
            {
                var replacement = active.ProgramId is { } programId
                    ? (await curriculum.NextAsync(user, programId, token) as CurriculumResult.Lesson)?.Value
                    : await learning.PracticeAsync(user, practice.SkillId!.Value, token);
                if (replacement is not (LearningResult.Material or LearningResult.Session { PendingExerciseId: not null }))
                    return new(PracticeReplyKind.ProviderUnavailable, context.Practice);
                var fresh = await lessons.FindActiveAsync(user, token)
                    ?? throw new InvalidOperationException("A fresh practice exercise is required.");
                context.ReplaceRevealedPracticeLesson(active, fresh);
                return new(PracticeReplyKind.Current, context.Practice, replacement);
            }
            return new(PracticeReplyKind.Current, context.Practice, operation == DialogueOperationKind.ResumePractice
                ? await learning.ResumeAsync(user, token) : LearningService.Session(active));
        }

        Lesson? previous = practice.LessonIds.Length == 0 ? null : await programs.FindPracticeLessonAsync(user, practice.LessonIds[^1], token);
        if (previous is not null && previous.Status != LessonStatus.Completed)
        {
            context.StopPractice();
            return new(PracticeReplyKind.Stopped, context.Practice);
        }
        if (practice.LessonIds.Length == practice.RequestedCount)
        {
            context.CompletePractice(previous!);
            return new(PracticeReplyKind.Completed, context.Practice);
        }
        LearningResult? result;
        if (practice.LessonIds.Length == 0)
        {
            var started = await curriculum.NextAsync(user, token);
            result = (started as CurriculumResult.Lesson)?.Value;
            if (started is CurriculumResult.RouteCompleted &&
                (await programs.FindLastProgramLessonAsync(user, context.ProgramId, token))?.SkillId is { } skill)
                result = await learning.PracticeAsync(user, skill, token);
        }
        else result = await learning.PracticeAsync(user, practice.SkillId!.Value, token);
        if (result is not (LearningResult.Material or LearningResult.Session { PendingExerciseId: not null }))
            return new(PracticeReplyKind.ProviderUnavailable, context.Practice);
        var issued = await lessons.FindActiveAsync(user, token) ?? throw new InvalidOperationException("A generated exercise must exist before association.");
        context.IssuePracticeLesson(issued, previous);
        return new(PracticeReplyKind.Current, context.Practice, result);
    }

    public async Task<LearningPracticeResult?> SynchronizeAsync(UserId user, DialogueOperationKind operation, CancellationToken token)
    {
        if (operation is not (DialogueOperationKind.Answer or DialogueOperationKind.Finish or DialogueOperationKind.Pause or
            DialogueOperationKind.Resume or DialogueOperationKind.RevealSolution)) return null;
        var context = await programs.FindContextAsync(user, token);
        if (context?.Practice is not { Supported: true, Active: true } practice || practice.LessonIds.Length == 0) return null;
        var last = await programs.FindPracticeLessonAsync(user, practice.LessonIds[^1], token);
        if (last is null) return new(PracticeReplyKind.Unavailable, practice);
        if (last.Status == LessonStatus.Paused) context.PausePractice();
        else if (last.Status == LessonStatus.AwaitingAnswer && operation == DialogueOperationKind.Resume) context.ResumePractice();
        else if (last.Status == LessonStatus.Finished) context.StopPractice();
        else if (last.Status == LessonStatus.Completed && practice.LessonIds.Length == practice.RequestedCount) context.CompletePractice(last);
        return last.Status == LessonStatus.Completed
            ? new(context.Practice!.Status == LearningPracticeStatus.Completed ? PracticeReplyKind.Completed : PracticeReplyKind.ReadyNext, context.Practice)
            : null;
    }
}
