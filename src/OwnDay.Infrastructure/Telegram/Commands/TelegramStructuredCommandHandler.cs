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
                if (text.Length is 0 or > Project.MaxTitleLength) return ["Use /project <text> (up to 200 characters)."];
                var project = await service.CreateProjectAsync(command.UserId, text, token);
                return [$"Project #{project.Id} created: {project.Title}"];
            case "idea":
                if (text.Length is 0 or > SomedayMaybe.MaxTextLength) return ["Use /idea <text> (up to 200 characters)."];
                var idea = await service.CreateSomedayMaybeAsync(command.UserId, text, null, token);
                return [$"Idea #{idea.Id} created: {idea.Text}"];
            case "note":
                if (text.Length is 0 or > Reference.MaxTextLength) return ["Use /note <text> (up to 200 characters)."];
                var note = await service.CreateReferenceAsync(command.UserId, text, null, token);
                return [$"Note #{note.Id} created: {note.Text}"];
            case "wait":
                if (text.Length is 0 or > WaitingFor.MaxDescriptionLength) return ["Use /wait <text> (up to 200 characters)."];
                var waiting = await service.CreateWaitingForAsync(command.UserId, text, null, null, token);
                return [$"Waiting item #{waiting.Id} created: {waiting.Description}"];
            case "list":
                return ["Lists:\n/inbox — Inbox\n/tasks — Tasks\n/projects — Projects\n/ideas — Ideas\n/notes — Notes\n/waiting — Waiting"];
        }

        if (text.Length > 0)
        {
            return [$"Use /{command.Name} without arguments."];
        }

        return command.Name switch
        {
            "projects" => TelegramTextLists.Render("Projects", await service.GetActiveProjectsAsync(command.UserId, token),
                item => $"#{item.Id} {item.Title}"),
            "ideas" => TelegramTextLists.Render("Ideas", await service.GetActiveSomedayMaybesAsync(command.UserId, token),
                item => $"#{item.Id} {item.Text}"),
            "notes" => TelegramTextLists.Render("Notes", await service.GetActiveReferencesAsync(command.UserId, token),
                item => $"#{item.Id} {item.Text}"),
            "waiting" => TelegramTextLists.Render("Waiting", await service.GetActiveWaitingForsAsync(command.UserId, token),
                item => $"#{item.Id} {item.Description}" + (item.Source is null ? "" : $"; source: {item.Source}") +
                    (item.ProjectId is null ? "" : $"; project #{item.ProjectId}")),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
    }
}
