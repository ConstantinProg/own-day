using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public sealed partial class LearningService
{
    public async Task<LearningResult> ChangeFormatAsync(UserId user, ExerciseResponseFormat format, CancellationToken token)
    {
        ValidateUser(user, token);
        var active = await store.FindActiveAsync(user, token);
        if (active is not { Status: LessonStatus.AwaitingAnswer }) return new LearningResult.InvalidTransition();

        var choice = format switch
        {
            ExerciseResponseFormat.ShortText => (ChoiceFormat?)null,
            ExerciseResponseFormat.SingleChoice => ChoiceFormat.SingleChoice,
            ExerciseResponseFormat.MultipleChoice => ChoiceFormat.MultipleChoice,
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        var requiredKind = active.IsReview ? ExerciseEvidenceKind.Retrieval : active.AssessmentContent?.StructuredRubric?.Kind;
        if (requiredKind == ExerciseEvidenceKind.Recognition) requiredKind = null;
        if (active.Transfer is not null || active.ProjectActionId is not null || active.ExpectedAnswer is not null ||
            active.Content is { Id: not LearningContent.GeneratedExerciseId } || choice is not null && requiredKind is not null)
            return new LearningResult.FormatUnavailable();
        if (choice is not null && active.Choice?.Exercise.Format == choice && !active.Choice.SolutionSeen)
            return Session(active);

        var programId = active.ProgramId ?? active.PracticeProgramId;
        var program = curriculum is not null && programId is { } id ? await curriculum.FindAsync(user, id, token) : null;
        var practice = curriculum is null ? null : await curriculum.FindContextAsync(user, token);
        if (practice?.Practice is { Active: true } block && block.LessonIds.LastOrDefault() == active.Id &&
            block is not { Supported: true, Status: LearningPracticeStatus.Running })
            return new LearningResult.InvalidTransition();

        Lesson replacement;
        try
        {
            var status = active.SkillId is { } skillId ? await knowledge.StatusAsync(user, skillId, token) : null;
            var profile = await knowledge.PersonalizationAsync(user, token, program?.Discipline);
            string[] previous = [active.Question, .. active.SkillId is { } skill ? await store.RecentQuestionsAsync(user, skill, token) : []];
            var request = new LessonRequest(active.Topic, Program: program is null ? null : LessonProgramContext.From(program),
                IsReview: active.IsReview, Teaching: new(TeachingAction.Practice, "Keep the same skill and change the response format.",
                    status?.State ?? LearnerSkillState.Evaluate([]), []), Personalization: profile.ForGeneration())
            {
                RequestedFormat = format, PreferredChoice = choice, PracticeEvidenceKind = requiredKind, PreviousQuestions = previous
            };
            var material = await teacher.GenerateAsync(request, token);
            token.ThrowIfCancellationRequested();
            if (material.Choice?.Format != choice ||
                requiredKind is { } kind && !AdaptiveTeaching.IsValidFollowUp(material, previous, kind) ||
                AdaptiveTeaching.RepeatsQuestion(material.Question, previous))
                return new LearningResult.FormatUnavailable();
            replacement = await CreateWithChoiceHistoryAsync(user, active.Topic, material, token);
            if (replacement.Choice?.Reused == true) return new LearningResult.FormatUnavailable();
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new LearningResult.ProviderUnavailable();
        }

        active.ReplaceExerciseWith(replacement);
        if (practice?.Practice is { Supported: true, Status: LearningPracticeStatus.Running } current && current.LessonIds.LastOrDefault() == active.Id)
            practice.ReplacePracticeLesson(active, replacement);
        if (curriculum is not null) await curriculum.FlushAsync(token);
        store.Add(replacement);
        return Material(replacement) with { IncludeTheory = false };
    }
}
