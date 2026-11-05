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
        else
        {
            if (!string.IsNullOrWhiteSpace(command.Arguments)) return TelegramCurriculumPresenter.Render(new CurriculumResult.InvalidInput());
            result = command.Name == "learn_next" ? await service.NextAsync(command.UserId, token) : await service.ShowAsync(command.UserId, token);
        }
        return TelegramCurriculumPresenter.Render(result);
    }
}
