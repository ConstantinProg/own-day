using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramCurriculumPresenter
{
    public static IReadOnlyList<string> Render(CurriculumResult result) => result switch
    {
        CurriculumResult.Program program => RenderProgram(program.Value),
        CurriculumResult.Progress progress => TelegramProgressPresenter.Render(progress.Value),
        CurriculumResult.NoProgress empty => [empty.Page == 1
            ? "No programs yet. /learn_program <goal> | <experience>. Free lesson skills: /learn_skills"
            : "No program on this page. /learn_progress"],
        CurriculumResult.ProjectReport report => RenderProjectReport(report.Value),
        CurriculumResult.NoProject => ["This program has no project link. Create a project program: /learn_project <id from /projects> | <experience>"],
        CurriculumResult.ProjectCompleted => ["This project is completed. Project practice requires an active project. /learn_project_report"],
        CurriculumResult.ProjectActionNotFound => ["Active task not found in this program's project for this user. /learn_project_report"],
        CurriculumResult.Programs programs => RenderPrograms(programs),
        CurriculumResult.ProgramNotFound => ["Program not found for this user. /learn_programs"],
        CurriculumResult.TransferUnavailable => ["Interdisciplinary practice requires two owned programs in different disciplines, completed diagnosis and prerequisites, " +
            "and the current topic's shared skill explicitly confirmed with /learn_link. The other topic must use that same skill. " +
            "/learn_transfer <current topic code> | <other program id> | <other topic code>. /learn_programs · /learn_route · /learn_links"],
        CurriculumResult.LessonInProgress => ["Answer or finish the current lesson before starting another lesson or changing programs. /answer <answer> · /learn_resume · /learn_finish"],
        CurriculumResult.Linked linked => [RenderLink(linked.Link), "Shared identity confirmed by you. AI evidence and review schedule are reused; this does not complete this program's topics or prove mastery. /learn_links · /learn_next"],
        CurriculumResult.Links links => links.Items.Count == 0 ? ["No confirmed shared skills in this route. /learn_link <topic code> | <skill id from /learn_skills> | <reason>"] :
            [.. links.Items.Select(RenderLink), "/learn_transfer <current topic code> | <other program id from /learn_programs> | <other topic code>. Requires different disciplines and the same confirmed skill."],
        CurriculumResult.Lesson lesson => TelegramLearningPresenter.Render(lesson.Value),
        CurriculumResult.Adapted adapted => ["Teaching adjustment:\n" + adapted.Reason + "\n/learn_route",
            .. TelegramLearningPresenter.Render(adapted.Value)],
        CurriculumResult.NoProgram => ["Create a program: /learn_program <goal> | <experience>"],
        CurriculumResult.InvalidInput => [$"Use /learn_request <request> | <experience>, /learn_program <goal> | <experience>, or /learn_project <id> | <experience>. Goal: 1–{LearningProgram.MaxGoalLength} characters; experience: 1–{LearningProgram.MaxExperienceLength}. /learn_diagnose <answer up to {LearningProgram.MaxDiagnosticAnswerLength} characters or skip>. /learn_route, /learn_next and /learn_adapt take no arguments. /learn_focus <topic code from /learn_route> requires completed prerequisites.",
            $"/learn_programs [page]; /learn_select <program id>; /learn_links; /learn_link <topic code> | <skill id> | <reason, 1–{ProgramSkillLink.MaxReasonLength} characters>. /learn_project_report [page]; /learn_project_practice <topic code> | <active project task id>. Finish diagnosis first. Only your skills are allowed; a topic's existing identity cannot be replaced."],
        CurriculumResult.ProjectNotFound => ["Project not found for this user. /projects"],
        CurriculumResult.InvalidTransition => ["Diagnosis has already ended. /learn_route · /learn_next"],
        CurriculumResult.RouteCompleted => ["All route questions have been answered correctly according to AI. This does not establish long-term mastery. /learn_route"],
        CurriculumResult.ProviderUnavailable => ["The planner is temporarily unavailable or returned an invalid route. Retry the command."],
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    private static IReadOnlyList<string> RenderProjectReport(ProjectLearningReport report)
    {
        var messages = new List<string>
        {
            $"Project applicability: #{report.Project.Id} {report.Project.Title}\nProject status: {report.Project.Status}\n" +
            $"Learning goal: {report.Program.Goal}\nProgram: {report.Program.Id}; route v{report.Program.CurrentRoute.Version}\n" +
            "Topics below are AI-proposed skills for this project's goal. Applicability is provisional; educational answers do not prove project delivery.",
            "Practice leaves real tasks and project status unchanged. No Commitments are created. Complete real work separately after verifying it."
        };
        foreach (var item in report.Topics)
        {
            var state = item.Status?.State;
            var text = $"Suggested skill {item.Topic.Code}: {item.Topic.Title}\n" +
                (state is null ? "No assessment evidence yet." :
                    $"Provisional mastery: {state.Mastery}; {state.PositiveAssessments} positive, {state.NegativeAssessments} negative assessments.\n/learn_status {item.Status!.Skill.Id}");
            text += item.Available ? "\nAvailable for practice." : "\nFinish diagnosis or prerequisites before practice.";
            if (item.Available && report.Project.Status == OwnDay.Domain.Projects.ProjectStatus.Active)
                text += $"\n/learn_project_practice {item.Topic.Code} | <task id below>";
            messages.Add(text);
        }
        messages.Add("Active project tasks (choose a task and an available topic; their specific relationship is a learning proposal):");
        messages.AddRange(report.Actions.Select(a => $"Task #{a.Id}: {a.Title}"));
        if (report.Actions.Count == 0) messages.Add("No active project tasks on this page. /tasks");
        if (report.Page > 1) messages.Add($"Previous tasks: /learn_project_report {report.Page - 1}");
        if (report.HasNext) messages.Add($"Next tasks: /learn_project_report {report.Page + 1}");
        return messages;
    }

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
            : "Route ready. /learn_next · /learn_adapt · /learn_focus <topic code> · /learn_route",
            .. program.ProjectId is not null ? new[] { "Project skills and task practice: /learn_project_report [page] · /learn_project_practice <topic code> | <task id>" } : Array.Empty<string>()];
    }
}
