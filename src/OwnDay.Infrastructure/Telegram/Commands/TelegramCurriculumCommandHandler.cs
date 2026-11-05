using OwnDay.Application.Interactions;
using OwnDay.Application.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed class TelegramCurriculumCommandHandler(CurriculumService service)
{
    public bool Handles(string name) => name is "learn_request" or "learn_program" or "learn_project" or "learn_diagnose" or "learn_route" or "learn_next";

    public async Task<IReadOnlyList<string>> HandleAsync(ProcessIncomingCommand command, CancellationToken token)
    {
        CurriculumResult result;
        if (command.Name is "learn_request" or "learn_program" or "learn_project")
        {
            var parts = command.Arguments.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) return Render(new CurriculumResult.InvalidInput());
            long? projectId = null;
            if (command.Name == "learn_project")
            {
                if (!long.TryParse(parts[0], out var id) || id <= 0) return Render(new CurriculumResult.InvalidInput());
                projectId = id;
            }
            result = await service.CreateAsync(command.UserId, parts[0], parts[1], projectId, token);
        }
        else if (command.Name == "learn_diagnose")
        {
            result = await service.DiagnoseAsync(command.UserId, command.Arguments, token);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(command.Arguments)) return Render(new CurriculumResult.InvalidInput());
            result = command.Name == "learn_next" ? await service.NextAsync(command.UserId, token) : await service.ShowAsync(command.UserId, token);
        }
        return Render(result);
    }

    public static IReadOnlyList<string> Render(CurriculumResult result) => result switch
    {
        CurriculumResult.Program program => RenderProgram(program.Value),
        CurriculumResult.Lesson lesson => TelegramLearningPresenter.Render(lesson.Value),
        CurriculumResult.NoProgram => ["Create a program: /learn_program <goal> | <experience>"],
        CurriculumResult.InvalidInput => ["Use /learn_request <request> | <experience>, /learn_program <goal> | <experience>, or /learn_project <id> | <experience>. Goal: 1–500 characters; experience: 1–1000. /learn_diagnose <answer up to 3000 characters or skip>. /learn_route and /learn_next take no arguments."],
        CurriculumResult.ProjectNotFound => ["Project not found for this user. /projects"],
        CurriculumResult.InvalidTransition => ["Diagnosis has already ended. /learn_route · /learn_next"],
        CurriculumResult.RouteCompleted => ["All route questions have been answered correctly according to AI. This does not establish long-term mastery. /learn_route"],
        CurriculumResult.ProviderUnavailable => ["The planner is temporarily unavailable or returned an invalid route. Retry the command."],
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    private static IReadOnlyList<string> RenderProgram(OwnDay.Domain.Learning.LearningProgram program)
    {
        var route = program.CurrentRoute;
        var heading = $"Goal: {program.Goal}\nDiscipline: {program.Discipline}\nRoute v{route.Version}: {route.Reason}" +
            (program.ProjectId is long id ? $"\nProject: #{id}" : "");
        var topics = route.Topics.Select(t => $"{t.Code}: {t.Title}" +
            (t.Prerequisites.Count > 0 ? $" (requires: {string.Join(", ", t.Prerequisites)})" : ""));
        return [heading, .. topics.Chunk(4).Select(chunk => string.Join('\n', chunk)), program.DiagnosticPending
            ? $"Optional diagnostic (AI interpretation, not a knowledge score):\n{program.DiagnosticQuestion}\n/learn_diagnose <answer> or /learn_diagnose skip"
            : "Route ready. /learn_next · /learn_route"];
    }
}
