using OwnDay.Application.StructuredItems;
using OwnDay.Application.Interactions;
using OwnDay.Domain.Projects;
using OwnDay.Domain.References;
using OwnDay.Domain.SomedayMaybes;
using OwnDay.Domain.WaitingFors;
using OwnDay.Infrastructure.Telegram.Handling;

namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed class TelegramStructuredCommandHandler(StructuredItemService service)
{
    public bool Handles(string name) => name is "project" or "idea" or "note" or "wait" or
        "projects" or "ideas" or "notes" or "waiting" or "list";

    public async Task<IReadOnlyList<string>> HandleAsync(ProcessIncomingCommand command, CancellationToken token)
    {
        var text = command.Arguments.Trim();
        switch (command.Name)
        {
            case "project":
                if (text.Length is 0 or > Project.MaxTitleLength) return ["Используйте /project <текст> (до 200 символов)."];
                var project = await service.CreateProjectAsync(command.UserId, text, token);
                return [$"Проект #{project.Id} создан: {project.Title}"];
            case "idea":
                if (text.Length is 0 or > SomedayMaybe.MaxTextLength) return ["Используйте /idea <текст> (до 200 символов)."];
                var idea = await service.CreateSomedayMaybeAsync(command.UserId, text, null, token);
                return [$"Идея #{idea.Id} создана: {idea.Text}"];
            case "note":
                if (text.Length is 0 or > Reference.MaxTextLength) return ["Используйте /note <текст> (до 200 символов)."];
                var note = await service.CreateReferenceAsync(command.UserId, text, null, token);
                return [$"Заметка #{note.Id} создана: {note.Text}"];
            case "wait":
                if (text.Length is 0 or > WaitingFor.MaxDescriptionLength) return ["Используйте /wait <текст> (до 200 символов)."];
                var waiting = await service.CreateWaitingForAsync(command.UserId, text, null, null, token);
                return [$"Ожидание #{waiting.Id} создано: {waiting.Description}"];
            case "list":
                return ["Списки:\n/inbox — Входящие\n/tasks — Задачи\n/projects — Проекты\n/ideas — Идеи\n/notes — Заметки\n/waiting — Ожидаю"];
        }

        if (text.Length > 0)
        {
            return [$"Используйте /{command.Name} без аргументов."];
        }

        return command.Name switch
        {
            "projects" => TelegramTextLists.Render("Проекты", await service.GetActiveProjectsAsync(command.UserId, token),
                item => $"#{item.Id} {item.Title}"),
            "ideas" => TelegramTextLists.Render("Идеи", await service.GetActiveSomedayMaybesAsync(command.UserId, token),
                item => $"#{item.Id} {item.Text}"),
            "notes" => TelegramTextLists.Render("Заметки", await service.GetActiveReferencesAsync(command.UserId, token),
                item => $"#{item.Id} {item.Text}"),
            "waiting" => TelegramTextLists.Render("Ожидаю", await service.GetActiveWaitingForsAsync(command.UserId, token),
                item => $"#{item.Id} {item.Description}" + (item.Source is null ? "" : $"; источник: {item.Source}") +
                    (item.ProjectId is null ? "" : $"; проект #{item.ProjectId}")),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
    }
}
