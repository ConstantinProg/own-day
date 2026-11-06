using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramCurriculumPresenter
{
    public static IReadOnlyList<string> Render(CurriculumResult result) => result switch
    {
        CurriculumResult.Program program => RenderProgram(program.Value),
        CurriculumResult.Lesson lesson => TelegramLearningPresenter.Render(lesson.Value),
        CurriculumResult.Adapted adapted => ["Teaching adjustment:\n" + adapted.Reason + "\n/learn_route",
            .. TelegramLearningPresenter.Render(adapted.Value)],
        CurriculumResult.NoProgram => ["Create a program: /learn_program <goal> | <experience>"],
        CurriculumResult.InvalidInput => [$"Use /learn_request <request> | <experience>, /learn_program <goal> | <experience>, or /learn_project <id> | <experience>. Goal: 1–{LearningProgram.MaxGoalLength} characters; experience: 1–{LearningProgram.MaxExperienceLength}. /learn_diagnose <answer up to {LearningProgram.MaxDiagnosticAnswerLength} characters or skip>. /learn_route, /learn_next and /learn_adapt take no arguments. /learn_focus <topic code from /learn_route> requires completed prerequisites."],
        CurriculumResult.ProjectNotFound => ["Project not found for this user. /projects"],
        CurriculumResult.InvalidTransition => ["Diagnosis has already ended. /learn_route · /learn_next"],
        CurriculumResult.RouteCompleted => ["All route questions have been answered correctly according to AI. This does not establish long-term mastery. /learn_route"],
        CurriculumResult.ProviderUnavailable => ["The planner is temporarily unavailable or returned an invalid route. Retry the command."],
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    private static IReadOnlyList<string> RenderProgram(LearningProgram program)
    {
        var route = program.CurrentRoute;
        var heading = $"Goal: {program.Goal}\nDiscipline: {program.Discipline}\nRoute v{route.Version}: {route.Reason}" +
            (program.ProjectId is long id ? $"\nProject: #{id}" : "");
        var topics = route.Topics.Select(t => $"{t.Code}: {t.Title}" +
            (t.Prerequisites.Count > 0 ? $" (requires: {string.Join(", ", t.Prerequisites)})" : ""));
        return [heading, .. topics.Chunk(4).Select(chunk => string.Join('\n', chunk)), program.DiagnosticPending
            ? $"Optional diagnostic (AI interpretation, not a knowledge score):\n{program.DiagnosticQuestion}\n/learn_diagnose <answer> or /learn_diagnose skip"
            : "Route ready. /learn_next · /learn_adapt · /learn_focus <topic code> · /learn_route"];
    }
}
