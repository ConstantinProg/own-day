using OwnDay.Application.Dialogue;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public static class LearningActionProvider
{
    public static IReadOnlyList<LearningAction> ForDialogue(DialogueReplyKind kind) => kind switch
    {
        DialogueReplyKind.ConfirmFinish or DialogueReplyKind.ConfirmProgramSelection or DialogueReplyKind.ConfirmProgramCreation or
            DialogueReplyKind.ConfirmGoalIntent or DialogueReplyKind.ConfirmProgramSwitch or DialogueReplyKind.ConfirmGoalSwitch or
            DialogueReplyKind.ConfirmRouteEdit or DialogueReplyKind.ConfirmGoalUpdate => [new(LearningActionKind.Confirm), new(LearningActionKind.Reject)],
        DialogueReplyKind.ChooseAnswerOrQuestion => [new(LearningActionKind.ClarifyAnswer), new(LearningActionKind.ClarifyQuestion)],
        DialogueReplyKind.ChooseQuestionOrGoal => [new(LearningActionKind.ClarifyQuestion), new(LearningActionKind.ClarifyGoal)],
        DialogueReplyKind.ChoosePauseOrResume => [new(LearningActionKind.ClarifyPause), new(LearningActionKind.ClarifyContinue)],
        DialogueReplyKind.DiagnosticOffer => [new(LearningActionKind.StartDiagnosis), new(LearningActionKind.SkipDiagnosis)],
        DialogueReplyKind.DiagnosticQuestion => [new(LearningActionKind.SkipDiagnosis)],
        DialogueReplyKind.ConsultationRetry => [new(LearningActionKind.RetryConsultation)],
        DialogueReplyKind.AskExperience => [new(LearningActionKind.NoPriorExperience)],
        DialogueReplyKind.ConsultationPaused or DialogueReplyKind.NoActiveLesson or DialogueReplyKind.Paused or
            DialogueReplyKind.NeedBinding or DialogueReplyKind.SessionEnded or DialogueReplyKind.GoalChanged => [new(LearningActionKind.Continue)],
        DialogueReplyKind.MustEndPractice => [new(LearningActionKind.StopPractice)],
        _ => []
    };

    public static IReadOnlyList<LearningAction> ForPractice(PracticeReplyKind kind) => kind switch
    {
        PracticeReplyKind.ReadyNext => [new(LearningActionKind.Next), new(LearningActionKind.PausePractice), new(LearningActionKind.StopPractice)],
        PracticeReplyKind.Paused => [new(LearningActionKind.Continue), new(LearningActionKind.StopPractice)],
        PracticeReplyKind.ProviderUnavailable => [new(LearningActionKind.Next)],
        _ => []
    };

    public static IReadOnlyList<LearningAction> ForAssessment(Guid? lessonId, LearningContinuation? continuation)
    {
        var next = continuation switch
        {
            LearningContinuation.NextTopic => new LearningAction(LearningActionKind.Next),
            LearningContinuation.Review => new LearningAction(LearningActionKind.ReviewSuggestions),
            LearningContinuation.Practice => new LearningAction(LearningActionKind.AnotherExercise),
            _ => null
        };
        var actions = new List<LearningAction>(2);
        if (next is not null) actions.Add(next);
        if (lessonId is { } id) actions.Add(new(LearningActionKind.ExplainAssessment, id));
        return actions;
    }

    public static IReadOnlyList<LearningAction> ForExercise(bool choice = false) => choice
        ? [new(LearningActionKind.Hint), new(LearningActionKind.Solution)]
        : [new(LearningActionKind.Hint), new(LearningActionKind.Solution), new(LearningActionKind.Criteria)];

    public static IReadOnlyList<LearningAction> ForReview(Guid skillId, bool due) => due
        ? [new(LearningActionKind.StartReview, skillId), new(LearningActionKind.PostponeReview, skillId), new(LearningActionKind.SkipReview, skillId)]
        : [new(LearningActionKind.SkillPractice, skillId)];

    public static bool CanOffer(LearningAction action, Lesson? lesson) => action.Kind switch
    {
        LearningActionKind.Hint => lesson is { AcceptsAnswer: true } &&
            (lesson.Choice is { } choice ? !choice.SolutionSeen && choice.HintsUsed < choice.Exercise.Hints.Length : lesson.CanOfferMoreHelp),
        LearningActionKind.Solution => lesson is { AcceptsAnswer: true } &&
            (lesson.Choice is { } choice ? !choice.SolutionSeen : lesson.Interactions.All(interaction => interaction.Kind != LessonInteractionKind.FullAnswer)),
        LearningActionKind.Criteria => lesson is { AcceptsAnswer: true, AssessmentContent.StructuredRubric: not null },
        LearningActionKind.Next => lesson is null
            ? action.TargetId == Guid.Empty
            : lesson is { Status: LessonStatus.AwaitingAnswer, ExercisePending: true } && action.TargetId == lesson.Id,
        LearningActionKind.AnotherExercise or LearningActionKind.ProjectPractice or
            LearningActionKind.FocusTopic or LearningActionKind.StartReview or LearningActionKind.SkillPractice => lesson is null,
        _ => true
    };

    public static bool TargetsDialogue(LearningActionKind kind) =>
        kind is > LearningActionKind.SubmitChoice and not (LearningActionKind.StartInvitation or LearningActionKind.PostponeInvitation or
            LearningActionKind.SkipInvitation);

    public static bool MatchesDialogue(LearningAction action, DialogueContext? dialogue)
    {
        if (!TargetsDialogue(action.Kind)) return true;
        if (action.Kind is not (LearningActionKind.ProjectPractice or LearningActionKind.FocusTopic) &&
            action.Revision != (dialogue?.Revision ?? 0)) return false;
        return action.Kind switch
        {
            LearningActionKind.Confirm or LearningActionKind.Reject => dialogue?.PendingConfirmation?.ProposalId == action.TargetId,
            >= LearningActionKind.ClarifyAnswer and <= LearningActionKind.ClarifyContinue =>
                dialogue?.PendingClarification?.PromptId == action.TargetId,
            _ => true
        };
    }

    public static IReadOnlyList<LearningAction> ForChoice(ChoicePrompt prompt)
    {
        var multiple = prompt.Format == ChoiceFormat.MultipleChoice;
        var kind = multiple ? LearningActionKind.ToggleChoice : LearningActionKind.SelectChoice;
        var actions = prompt.Options.Select(option => LearningAction.ForChoice(prompt, kind, checked((byte)option.Id))).ToList();
        if (multiple && !prompt.Draft.IsEmpty)
            actions.Add(LearningAction.ForChoice(prompt, LearningActionKind.SubmitChoice));
        actions.AddRange(ForExercise(choice: true));
        return actions;
    }
}
