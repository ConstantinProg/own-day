using OwnDay.Application.Interactions;
using OwnDay.Application.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed class TelegramCurriculumCommandHandler(CurriculumService service)
{
    public bool Handles(string name) => name is "learn_request" or "learn_program" or "learn_project" or "learn_diagnose" or "learn_route" or "learn_next" or "learn_adapt" or "learn_focus"
        or "learn_programs" or "learn_select" or "learn_link" or "learn_links" or "learn_project_report" or "learn_project_practice";

    public async Task<IReadOnlyList<string>> HandleAsync(ProcessIncomingCommand command, CancellationToken token)
    {
        CurriculumResult result;
        if (command.Name is "learn_programs" or "learn_project_report")
        {
            var page = 1;
            if (!string.IsNullOrWhiteSpace(command.Arguments) && !int.TryParse(command.Arguments, out page))
                return TelegramCurriculumPresenter.Render(new CurriculumResult.InvalidInput());
            result = command.Name == "learn_programs" ? await service.ListAsync(command.UserId, page, token)
                : await service.ProjectReportAsync(command.UserId, page, token);
        }
        else if (command.Name == "learn_project_practice")
        {
            var parts = command.Arguments.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !long.TryParse(parts[1], out var id))
                return TelegramCurriculumPresenter.Render(new CurriculumResult.InvalidInput());
            result = await service.ProjectPracticeAsync(command.UserId, parts[0], id, token);
        }
        else if (command.Name == "learn_select")
        {
            if (!Guid.TryParse(command.Arguments.Trim(), out var id)) return TelegramCurriculumPresenter.Render(new CurriculumResult.InvalidInput());
            result = await service.SelectAsync(command.UserId, id, token);
        }
        else if (command.Name == "learn_link")
        {
            var parts = command.Arguments.Split('|', 3, StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || !Guid.TryParse(parts[1], out var id)) return TelegramCurriculumPresenter.Render(new CurriculumResult.InvalidInput());
            result = await service.LinkAsync(command.UserId, parts[0], id, parts[2], token);
        }
        else if (command.Name is "learn_request" or "learn_program" or "learn_project")
        {
            var parts = command.Arguments.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) return TelegramCurriculumPresenter.Render(new CurriculumResult.InvalidInput());
            long? projectId = null;
            if (command.Name == "learn_project")
            {
                if (!long.TryParse(parts[0], out var id) || id <= 0) return TelegramCurriculumPresenter.Render(new CurriculumResult.InvalidInput());
                projectId = id;
            }
            result = await service.CreateAsync(command.UserId, parts[0], parts[1], projectId, token);
        }
        else if (command.Name == "learn_diagnose")
        {
            result = await service.DiagnoseAsync(command.UserId, command.Arguments, token);
        }
        else if (command.Name == "learn_focus")
        {
            var code = command.Arguments.Trim();
            if (code.Length is < 1 or > OwnDay.Domain.Learning.CurriculumTopic.MaxCodeLength)
                return TelegramCurriculumPresenter.Render(new CurriculumResult.InvalidInput());
            result = await service.FocusAsync(command.UserId, code, token);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(command.Arguments)) return TelegramCurriculumPresenter.Render(new CurriculumResult.InvalidInput());
            result = command.Name switch
            {
                "learn_next" => await service.NextAsync(command.UserId, token),
                "learn_adapt" => await service.AdaptAsync(command.UserId, token),
                "learn_links" => await service.LinksAsync(command.UserId, token),
                _ => await service.ShowAsync(command.UserId, token)
            };
        }
        return TelegramCurriculumPresenter.Render(result);
    }
}
