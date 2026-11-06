using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramCurriculumPresenter
{
    public static IReadOnlyList<string> Render(CurriculumResult result) => result switch
    {
        CurriculumResult.Program program => RenderProgram(program.Value),
        CurriculumResult.Programs programs => RenderPrograms(programs),
        CurriculumResult.ProgramNotFound => ["Program not found for this user. /learn_programs"],
        CurriculumResult.LessonInProgress => ["Answer or finish the current lesson before changing programs. /answer <answer> · /learn_resume · /learn_finish"],
        CurriculumResult.Linked linked => [RenderLink(linked.Link), "Shared identity confirmed by you. AI evidence and review schedule are reused; this does not complete this program's topics or prove mastery. /learn_links · /learn_next"],
        CurriculumResult.Links links => links.Items.Count == 0 ? ["No confirmed shared skills in this route. /learn_link <topic code> | <skill id from /learn_skills> | <reason>"] : links.Items.Select(RenderLink).ToArray(),
        CurriculumResult.Lesson lesson => TelegramLearningPresenter.Render(lesson.Value),
        CurriculumResult.Adapted adapted => ["Teaching adjustment:\n" + adapted.Reason + "\n/learn_route",
            .. TelegramLearningPresenter.Render(adapted.Value)],
        CurriculumResult.NoProgram => ["Create a program: /learn_program <goal> | <experience>"],
        CurriculumResult.InvalidInput => [$"Use /learn_request <request> | <experience>, /learn_program <goal> | <experience>, or /learn_project <id> | <experience>. Goal: 1–{LearningProgram.MaxGoalLength} characters; experience: 1–{LearningProgram.MaxExperienceLength}. /learn_diagnose <answer up to {LearningProgram.MaxDiagnosticAnswerLength} characters or skip>. /learn_route, /learn_next and /learn_adapt take no arguments. /learn_focus <topic code from /learn_route> requires completed prerequisites.",
            $"/learn_programs [page]; /learn_select <program id>; /learn_links; /learn_link <topic code> | <skill id> | <reason, 1–{ProgramSkillLink.MaxReasonLength} characters>. Finish diagnosis first. Only your skills are allowed; a topic's existing identity cannot be replaced."],
        CurriculumResult.ProjectNotFound => ["Project not found for this user. /projects"],
        CurriculumResult.InvalidTransition => ["Diagnosis has already ended. /learn_route · /learn_next"],
        CurriculumResult.RouteCompleted => ["All route questions have been answered correctly according to AI. This does not establish long-term mastery. /learn_route"],
        CurriculumResult.ProviderUnavailable => ["The planner is temporarily unavailable or returned an invalid route. Retry the command."],
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    private static string RenderLink(ProgramSkillLink link) =>
        $"{link.TopicCode} → skill {link.SkillId}\nSource: {link.Source}\nTrust: {link.Trust} (identity only, not proof of mastery)\nConfirmed: {link.ConfirmedAt:O}\nReason: {link.Reason}";

    private static IReadOnlyList<string> RenderPrograms(CurriculumResult.Programs page)
    {
        var messages = page.Items.Select(p => $"{p.Goal}\nDiscipline: {p.Discipline}\nRoute v{p.CurrentRoute.Version}\n/learn_select {p.Id}").ToList();
        if (messages.Count == 0) messages.Add("No programs on this page. /learn_program <goal> | <experience>");
        if (page.Page > 1) messages.Add($"Previous: /learn_programs {page.Page - 1}");
        if (page.HasNext) messages.Add($"Next: /learn_programs {page.Page + 1}");
        return messages;
    }

    private static IReadOnlyList<string> RenderProgram(LearningProgram program)
    {
        var route = program.CurrentRoute;
        var heading = $"Goal: {program.Goal}\nProgram: {program.Id}\nDiscipline: {program.Discipline}\nRoute v{route.Version}: {route.Reason}" +
            (program.ProjectId is long id ? $"\nProject: #{id}" : "");
        var topics = route.Topics.Select(t => $"{t.Code}: {t.Title}" +
            (t.Prerequisites.Count > 0 ? $" (requires: {string.Join(", ", t.Prerequisites)})" : ""));
        return [heading, .. topics.Chunk(4).Select(chunk => string.Join('\n', chunk)), program.DiagnosticPending
            ? $"Optional diagnostic (AI interpretation, not a knowledge score):\n{program.DiagnosticQuestion}\n/learn_diagnose <answer> or /learn_diagnose skip"
            : "Route ready. /learn_next · /learn_adapt · /learn_focus <topic code> · /learn_route"];
    }
}
