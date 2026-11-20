using OwnDay.Application.Dialogue;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramCurriculumPresenter
{
    public static IReadOnlyList<TelegramLearningReply> Render(CurriculumResult result, bool inRussian = false) => result switch
    {
        CurriculumResult.Program program => program.ResumeNotice is null ? [.. RenderProgram(program.Value)] :
            [.. RenderProgram(program.Value), program.ResumeNotice],
        CurriculumResult.RouteView route => RenderRouteActions(route),
        CurriculumResult.RouteHistory history => ["Последние версии маршрута. Текстом — /learn_route tree <версия>.",
            .. history.Routes.Select(route => TelegramLearningReply.WithActions($"v{route.Version} — {route.Reason}",
                [LearningAction.ForNumber(LearningActionKind.RouteVersion, route.Version)], inRussian: true))],
        CurriculumResult.RouteEditRejected => ["Изменение или версия недоступны. Проверьте активную программу, текущую версию, коды тем и порядок предпосылок. /learn_route tree · /learn_route history"],
        CurriculumResult.GoalEditRejected => ["Обновление цели недоступно. Укажите цель в рамках текущей дисциплины и наблюдаемый результат через /learn_goal_update <цель> | <критерий>. Для отдельной программы напишите «сменить цель на <цель>»."],
        CurriculumResult.GoalUpdateOffered proposal => ["Предложение для текущей программы. Действующие упражнения и оценки сохраняются. " +
            (proposal.RetainsRouteCredit ? "Зачёт по тем же темам переносится." : "У нового набора тем будет собственный зачёт."),
            .. RenderRoute(new(proposal.Route, true), preview: true)],
        CurriculumResult.Diagnostic diagnostic => [.. RenderDiagnostic(diagnostic)],
        CurriculumResult.SubjectDiagnosticQuestion diagnostic => RenderSubjectDiagnostic(diagnostic),
        CurriculumResult.Progress progress => [.. TelegramProgressPresenter.Render(progress.Value)],
        CurriculumResult.NoProgress empty => [empty.Page == 1
            ? "No programs yet. /learn_program <goal> | <experience>. Free lesson skills: /learn_skills"
            : "No program on this page. /learn_progress"],
        CurriculumResult.ProjectReport report => [.. RenderProjectReport(report.Value)],
        CurriculumResult.NoProject => ["This program has no project link. Create a project program: /learn_project <id from /projects> | <experience>"],
        CurriculumResult.ProjectCompleted => ["This project is completed. Project practice requires an active project. /learn_project_report"],
        CurriculumResult.ProjectActionNotFound => ["Active task not found in this program's project for this user. /learn_project_report"],
        CurriculumResult.Programs programs => [.. RenderPrograms(programs)],
        CurriculumResult.ProgramNotFound => ["Program not found for this user. /learn_programs"],
        CurriculumResult.TransferUnavailable => ["Interdisciplinary practice requires two owned programs in different disciplines, completed diagnosis and prerequisites, " +
            "and the current topic's shared skill explicitly confirmed with /learn_link. The other topic must use that same skill. " +
            "/learn_transfer <current topic code> | <other program id> | <other topic code>. /learn_programs · /learn_route · /learn_links"],
        CurriculumResult.LessonInProgress => ["Answer or finish the current lesson before starting another lesson or changing programs. /answer <answer> · /learn_resume · /learn_finish"],
        CurriculumResult.Linked linked => [RenderLink(linked.Link), "Shared identity confirmed by you. AI evidence and review schedule are reused; this does not complete this program's topics or prove mastery. /learn_links · /learn_next"],
        CurriculumResult.Links links => links.Items.Count == 0 ? ["No confirmed shared skills in this route. /learn_link <topic code> | <skill id from /learn_skills> | <reason>"] :
            [.. links.Items.Select(RenderLink), "/learn_transfer <current topic code> | <other program id from /learn_programs> | <other topic code>. Requires different disciplines and the same confirmed skill."],
        CurriculumResult.Lesson lesson => lesson.Introduction is null ? TelegramLearningPresenter.Render(lesson.Value)
            : [lesson.Introduction, .. TelegramLearningPresenter.Render(lesson.Value)],
        CurriculumResult.Adapted { Value: LearningResult.Material { Choice: not null } } choice =>
            TelegramLearningPresenter.Render(choice.Value),
        CurriculumResult.Adapted adapted => ["Teaching adjustment:\n" + adapted.Reason + "\n/learn_route",
            .. TelegramLearningPresenter.Render(adapted.Value)],
        CurriculumResult.NoProgram => ["Create a program: /learn_program <goal> | <experience>"],
        CurriculumResult.InvalidInput => [$"Use /learn_request <request> | <experience>, /learn_program <goal> | <experience>, or /learn_project <id> | <experience>. Goal: 1–{LearningProgram.MaxGoalLength} characters; experience: 1–{LearningProgram.MaxExperienceLength}. /learn_diagnose <answer up to {LearningProgram.MaxDiagnosticAnswerLength} characters or skip>. /learn_route, /learn_next and /learn_adapt take no arguments. /learn_focus <topic code from /learn_route> requires completed prerequisites.",
            $"/learn_programs [page]; /learn_select <program id>; /learn_links; /learn_link <topic code> | <skill id> | <reason, 1–{ProgramSkillLink.MaxReasonLength} characters>. /learn_project_report [page]; /learn_project_practice <topic code> | <active project task id>. Finish diagnosis first. Only your skills are allowed; a topic's existing identity cannot be replaced."],
        CurriculumResult.ProjectNotFound => ["Project not found for this user. /projects"],
        CurriculumResult.InvalidTransition => ["Diagnosis has already ended. /learn_route · /learn_next"],
        CurriculumResult.RouteCompleted => [TelegramLearningReply.WithActions(
            "В текущем маршруте больше нет доступных тем. Текстом — /learn_progress или /learn_route tree.",
            [new(LearningActionKind.Progress), new(LearningActionKind.RouteTree)], inRussian: true)],
        CurriculumResult.ProviderUnavailable => [inRussian ? "Пока не удалось подготовить следующий шаг. Попробуйте ещё раз чуть позже."
            : "I couldn't prepare the next step. Try again in a moment."],
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    private static IReadOnlyList<TelegramLearningReply> RenderRouteActions(CurriculumResult.RouteView view)
    {
        var text = RenderRoute(view);
        var actions = new List<LearningAction> { new(LearningActionKind.RouteHistory) };
        if (!view.Full) actions.Insert(0, new(LearningActionKind.RouteTree));
        if (view.OptionalCheck is { } code)
        {
            var index = view.Route.Topics.ToList().FindIndex(topic => topic.Code == code);
            if (index >= 0)
                actions.Add(LearningAction.ForNumber(LearningActionKind.FocusTopic, 0, view.Route.Version, index) with
                    { Revision = LearningAction.TopicIdentity(view.Route.ProgramId, view.Route.Version, code) });
            actions.Add(new(LearningActionKind.Next));
        }
        return [.. text.SkipLast(1), TelegramLearningReply.WithActions(text[^1], actions, inRussian: true)];
    }

    private static IReadOnlyList<TelegramLearningReply> RenderSubjectDiagnostic(CurriculumResult.SubjectDiagnosticQuestion diagnostic)
    {
        var russian = MarketLearningContent.InRussian(diagnostic.Value.Goal);
        var text = (diagnostic.Notice is null ? "" : diagnostic.Notice + "\n\n") + diagnostic.Value.DiagnosticQuestion! +
            (russian ? "\nОтветьте текстом. Чтобы пропустить, можно написать «пропустить»." : "\nReply with your answer, or write “skip”.");
        return [TelegramLearningReply.WithActions(text, [new(LearningActionKind.SkipDiagnosis)], russian)];
    }

    private static IReadOnlyList<string> RenderRoute(CurriculumResult.RouteView view, bool preview = false)
    {
        var route = view.Route;
        var heading = preview ? $"Предлагаемый маршрут v{route.Version}" : view.Full ? $"Маршрут v{route.Version}\n{route.Reason}" : "Маршрут";
        if (route.Path?.GoalChange?.Effective is { } goal)
            heading += $"\nЦель — {DisplayGoal(goal.Description)}" + (goal.Criterion is not null ? $"\nКритерий — {DisplayGoal(goal.Criterion)}" : "");
        if (view.Full && route.Path?.RestoredFrom is { } restoredFrom) heading += $"\nВосстановлен из v{restoredFrom}.";
        var topics = route.Topics.Select(topic =>
        {
            var line = view.Full ? $"{topic.Title} ({topic.Code})" : topic.Title;
            if (route.IsSkipped(topic.Code)) line += " · пропущена";
            if (view.Full && topic.Prerequisites.Count > 0) line += $" · предпосылки: {string.Join(", ", topic.Prerequisites)}";
            if (view.Full && route.DepthFor(topic.Code) is CurriculumDepth.Brief) line += " · кратко";
            if (view.Full && route.DepthFor(topic.Code) is CurriculumDepth.Detailed) line += " · подробно";
            return line;
        });
        return [heading, .. topics.Chunk(4).Select(chunk => string.Join('\n', chunk)), .. preview ? [] : new[]
            { view.OptionalCheck is { } code
                ? $"Эта тема служит предпосылкой. Можно продолжить с /learn_next или выбрать короткую проверку после текущего упражнения: /learn_focus {code}."
                : "Напишите «покажи дерево тем» или «история маршрута»." }];
    }

    private static IReadOnlyList<TelegramLearningReply> RenderDiagnostic(CurriculumResult.Diagnostic result)
    {
        if (result.Paused)
            return [TelegramLearningReply.WithActions(result.Notice!, [new(LearningActionKind.Continue)], inRussian: true)];
        var diagnostic = result.Value.EnglishDiagnostic!;
        var notice = result.Notice is null ? "" : result.Notice + "\n\n";
        if (diagnostic.Status == EnglishDiagnosticStatus.Offered)
            return [TelegramLearningReply.WithActions(notice + "Два коротких задания на чтение и письмо дадут предварительное представление о навыках. " +
                "Текстом — «начнём диагностику» или «пропустим». Уведомления не включаются.",
                LearningActionProvider.ForDialogue(DialogueReplyKind.DiagnosticOffer), inRussian: true)];
        if (diagnostic.Status != EnglishDiagnosticStatus.Started)
            return [TelegramLearningReply.WithActions(notice + "Диагностика завершена или пропущена. Ответы сохранены. Текстом — «Продолжим».",
                [new(LearningActionKind.Continue)], inRussian: true)];
        var index = diagnostic.Results.Length;
        return [TelegramLearningReply.WithActions(
            notice + $"Задание {index + 1} из 2.\n" + EnglishLearningContent.Text(index) + "\n\n" + EnglishLearningContent.Question(index) +
            "\n\nТекстом — «помощь», «пропустим» или «пауза». Ответы с помощью отмечаются отдельно.",
            [new(LearningActionKind.DiagnosisHelp), new(LearningActionKind.SkipDiagnosis), new(LearningActionKind.PauseDiagnosis)], inRussian: true)];
    }

    private static IReadOnlyList<TelegramLearningReply> RenderProjectReport(ProjectLearningReport report)
    {
        var messages = new List<TelegramLearningReply>
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
        messages.AddRange(report.Actions.Select(task => TelegramLearningReply.WithActions($"Task #{task.Id}: {task.Title}",
            ProjectPracticeActionProvider.ForTask(report, task.Id), inRussian: false)));
        if (report.Actions.Count == 0) messages.Add("No active project tasks on this page.");
        if (report.Page > 1) messages.Add(Page("Previous tasks", "learn_project_report", LearningActionKind.ProjectReportPage, report.Page - 1));
        if (report.HasNext) messages.Add(Page("Next tasks", "learn_project_report", LearningActionKind.ProjectReportPage, report.Page + 1));
        return messages;
    }

    private static string RenderLink(ProgramSkillLink link) =>
        $"{link.TopicCode} → skill {link.SkillId}\nSource: {link.Source}\nTrust: {link.Trust} (identity only, not proof of mastery)\nConfirmed: {link.ConfirmedAt:O}\nReason: {link.Reason}";

    private static IReadOnlyList<TelegramLearningReply> RenderPrograms(CurriculumResult.Programs page)
    {
        var messages = page.Items.Select(program => TelegramLearningReply.WithActions(
            $"{DisplayGoal(program.Goal)}\nDiscipline: {program.Discipline}\nRoute v{program.CurrentRoute.Version}\n/learn_select {program.Id}",
            [new(LearningActionKind.SelectProgram, program.Id)], inRussian: false)).ToList();
        if (messages.Count == 0) messages.Add("No programs on this page. /learn_program <goal> | <experience>");
        if (page.Page > 1) messages.Add(Page("Previous", "learn_programs", LearningActionKind.ProgramsPage, page.Page - 1));
        if (page.HasNext) messages.Add(Page("Next", "learn_programs", LearningActionKind.ProgramsPage, page.Page + 1));
        return messages;
    }

    private static TelegramLearningReply Page(string caption, string command, LearningActionKind kind, int number) =>
        TelegramLearningReply.WithActions($"{caption} — /{command} {number}", [LearningAction.ForNumber(kind, number)], inRussian: false);

    private static IReadOnlyList<TelegramLearningReply> RenderProgram(LearningProgram program)
    {
        var route = program.CurrentRoute;
        var heading = $"Goal: {DisplayGoal(program.Goal)}\nProgram: {program.Id}\nDiscipline: {program.Discipline}\nRoute v{route.Version}: {route.Reason}" +
            (program.GoalCriterion is { } criterion ? $"\nКритерий цели — {DisplayGoal(criterion)}" : "") +
            (program.ProjectId is long id ? $"\nProject: #{id}" : "");
        var topics = route.Topics.Select(t => $"{t.Code}: {t.Title}" +
            (t.Prerequisites.Count > 0 ? $" (requires: {string.Join(", ", t.Prerequisites)})" : ""));
        var actions = new List<LearningAction>();
        var next = "Route ready. /learn_next · /learn_adapt · /learn_focus <topic code> · /learn_route";
        if (program.DiagnosticPending)
        {
            next = $"Optional diagnostic (AI interpretation, not a knowledge score):\n{program.DiagnosticQuestion}\n/learn_diagnose <answer> or /learn_diagnose skip";
            actions.Add(new(LearningActionKind.SkipDiagnosis));
        }
        else
        {
            actions.Add(new(LearningActionKind.Next));
            actions.Add(new(LearningActionKind.RouteTree));
            if (program.SubjectDiagnostic is { Deferred: true })
            {
                next += "\nDeferred diagnosis: /learn_diagnose start";
                actions.Add(new(LearningActionKind.StartDiagnosis));
            }
        }
        var russian = MarketLearningContent.InRussian(program.Goal);
        var replies = new List<TelegramLearningReply> { heading };
        replies.AddRange(topics.Chunk(4).Select(chunk => new TelegramLearningReply(string.Join('\n', chunk))));
        replies.Add(TelegramLearningReply.WithActions(next, actions, russian));
        if (program.ProjectId is not null)
            replies.Add(TelegramLearningReply.WithActions(
                "Project skills and task practice: /learn_project_report [page] · /learn_project_practice <topic code> | <task id>",
                [LearningAction.ForNumber(LearningActionKind.ProjectReportPage, 1)], inRussian: false));
        return replies;
    }

    internal static string DisplayGoal(string text) => LearningGoalText.Display(text);
}
