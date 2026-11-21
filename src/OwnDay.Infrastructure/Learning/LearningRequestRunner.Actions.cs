using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Dialogue;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Learning;

public sealed partial class LearningRequestRunner
{
    private static async Task<(LearningCommandInput? Command, LearningTextInput? Text)> BindDialogueActionAsync(
        IServiceProvider services, LearningRequest request, LearningActionInput input, DialogueContext? dialogue, Lesson? lesson,
        CancellationToken token)
    {
        var action = input.Action;
        var revision = dialogue?.Revision;
        var prompt = dialogue?.PromptId;
        var lessonId = lesson?.Id;
        var id = action.TargetId.ToString("D");
        var (number, routeVersion, topicIndex) = (action.Number, action.RouteVersion, action.TopicIndex);
        if (action.Kind is LearningActionKind.Hint or LearningActionKind.Solution or LearningActionKind.Criteria or
            LearningActionKind.ExplainMaterial or LearningActionKind.ShowExample && action.TargetId != lesson?.Id)
            return default;

        var phrase = action.Kind switch
        {
            LearningActionKind.AnotherExercise => "another exercise",
            LearningActionKind.ClarifyAnswer => "answer",
            LearningActionKind.ClarifyQuestion => "question",
            LearningActionKind.ClarifyGoal => "goal",
            LearningActionKind.ClarifyPause => "pause",
            LearningActionKind.ClarifyContinue => "resume",
            LearningActionKind.RetryConsultation => "retry",
            LearningActionKind.NoPriorExperience => FirstGoalConsultation.InEnglish(dialogue?.Consultation?.OriginalGoal) ? "no experience" : "с нуля",
            LearningActionKind.StopPractice => "хватит практики",
            _ => null
        };
        if (phrase is not null)
            return (null, new(phrase, lessonId, prompt, revision, ProgramSwitchAllowed: input.ProgramSwitchAllowed));

        (string? Name, string Arguments) binding = action.Kind switch
        {
            LearningActionKind.Continue => ("learn_resume", ""),
            LearningActionKind.Next => ("learn_next", ""),
            LearningActionKind.Hint => ("learn_ask", input.InRussian ? "Дай короткую подсказку." : "Give me a short hint."),
            LearningActionKind.Solution => ("learn_solution", ""),
            LearningActionKind.Criteria => ("learn_explain", ""),
            LearningActionKind.ExplainMaterial when lesson?.ExercisePending == true => ("learn_ask",
                input.InRussian ? "Объясни этот принцип подробнее." : "Explain this idea in more detail."),
            LearningActionKind.ExplainMaterial => ("learn_explain", ""),
            LearningActionKind.ShowExample => ("learn_ask", input.InRussian ? "Покажи пример." : "Show me an example."),
            LearningActionKind.ExplainAssessment when action.TargetId != Guid.Empty => ("learn_assessment", id),
            LearningActionKind.ReviewSuggestions => ("learn_recommend_reviews", ""),
            LearningActionKind.Confirm or LearningActionKind.Reject => ("learn_confirm", dialogue!.PendingConfirmation!.Code),
            LearningActionKind.StartDiagnosis => ("learn_diagnose", "start"),
            LearningActionKind.SkipDiagnosis => ("learn_diagnose", "skip"),
            LearningActionKind.DiagnosisHelp => ("learn_diagnose", "help"),
            LearningActionKind.PauseDiagnosis or LearningActionKind.PausePractice => ("learn_pause", ""),
            LearningActionKind.StartInvitation => ("learn_invite_start", id),
            LearningActionKind.PostponeInvitation => ("learn_invite_later", id),
            LearningActionKind.SkipInvitation => ("learn_invite_skip", id),
            LearningActionKind.PauseReminders => ("learn_notify_pause", ""),
            LearningActionKind.ResumeReminders => ("learn_notify_resume", ""),
            LearningActionKind.StopReminders => ("learn_notify_off", ""),
            LearningActionKind.SelectProgram => ("learn_select", id),
            LearningActionKind.RouteTree => ("learn_route", "tree"),
            LearningActionKind.RouteHistory => ("learn_route", "history"),
            LearningActionKind.Progress => ("learn_progress", ""),
            LearningActionKind.RouteVersion => ("learn_route", FormattableString.Invariant($"tree {number}")),
            LearningActionKind.StartReview => ("learn_review", id),
            LearningActionKind.PostponeReview => ("learn_review_later", id),
            LearningActionKind.SkipReview => ("learn_review_skip", id),
            LearningActionKind.SkillPractice => ("learn_practice", id),
            LearningActionKind.ProjectReportPage => ("learn_project_report", number.ToString(CultureInfo.InvariantCulture)),
            LearningActionKind.ProgramsPage => ("learn_programs", number.ToString(CultureInfo.InvariantCulture)),
            LearningActionKind.ReviewsPage => ("learn_reviews", number.ToString(CultureInfo.InvariantCulture)),
            _ => (null, "")
        };

        if (action.Kind is LearningActionKind.ProjectPractice or LearningActionKind.FocusTopic)
        {
            var current = await services.GetRequiredService<ICurriculumStore>().FindAsync(request.UserId, token);
            if (current?.CurrentRoute is not { } route || route.Version != routeVersion ||
                topicIndex < 0 || topicIndex >= route.Topics.Count)
                return default;
            var topic = route.Topics[topicIndex].Code;
            if (action.Revision != (LearningAction.TopicIdentity(current.Id, routeVersion, topic) ^ (dialogue?.Revision ?? 0)))
                return default;
            binding = action.Kind == LearningActionKind.ProjectPractice
                ? ("learn_project_practice", FormattableString.Invariant($"{topic} | {number}")) : ("learn_focus", topic);
        }

        if (action.Kind is LearningActionKind.PauseReminders or LearningActionKind.ResumeReminders or LearningActionKind.StopReminders)
        {
            var schedule = await services.GetRequiredService<IStudyScheduleStore>().FindAsync(request.UserId, token);
            if (!StudyActionProvider.ForSchedule(schedule).Any(available => available.Kind == action.Kind)) return default;
        }

        return binding.Name is { } name
            ? (new(name, binding.Arguments, lessonId, prompt, revision, input.ProgramSwitchAllowed), null) : default;
    }

    private static bool RemovesKeyboard(LearningActionKind kind) => kind is
        LearningActionKind.Confirm or LearningActionKind.Reject or >= LearningActionKind.ClarifyAnswer and <= LearningActionKind.ClarifyContinue or
        LearningActionKind.Next or LearningActionKind.AnotherExercise or
        LearningActionKind.StartDiagnosis or LearningActionKind.SkipDiagnosis or LearningActionKind.DiagnosisHelp or LearningActionKind.PauseDiagnosis or
        LearningActionKind.PausePractice or LearningActionKind.StopPractice or LearningActionKind.RetryConsultation or LearningActionKind.NoPriorExperience or
        LearningActionKind.PauseReminders or LearningActionKind.ResumeReminders or LearningActionKind.StopReminders or
        LearningActionKind.SelectProgram or LearningActionKind.FocusTopic or LearningActionKind.ProjectPractice or LearningActionKind.SkillPractice or
        LearningActionKind.StartReview or LearningActionKind.PostponeReview or LearningActionKind.SkipReview;
}
