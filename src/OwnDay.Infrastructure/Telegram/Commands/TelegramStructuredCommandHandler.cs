using OwnDay.Application.StructuredItems;
using OwnDay.Application.Interactions;
using OwnDay.Domain.Projects;
using OwnDay.Domain.References;
using OwnDay.Domain.SomedayMaybes;
using OwnDay.Domain.WaitingFors;
using OwnDay.Infrastructure.Telegram.Handling;
using OwnDay.Infrastructure.Telegram.Localization;

namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed class TelegramStructuredCommandHandler(StructuredItemService service)
{
    public bool Handles(string name) => name is "project" or "idea" or "note" or "wait" or
        "projects" or "ideas" or "notes" or "waiting" or "list";

    public async Task<IReadOnlyList<string>> HandleAsync(ProcessIncomingCommand command, string locale, CancellationToken token)
    {
        var text = command.Arguments.Trim();
        switch (command.Name)
        {
            case "project":
                if (text.Length is 0 or > Project.MaxTitleLength) return [TelegramTexts.Get(locale, "structured.project.usage")];
                var project = await service.CreateProjectAsync(command.UserId, text, token);
                return [TelegramTexts.Get(locale, "structured.project.created", project.Id, project.Title)];
            case "idea":
                if (text.Length is 0 or > SomedayMaybe.MaxTextLength) return [TelegramTexts.Get(locale, "structured.idea.usage")];
                var idea = await service.CreateSomedayMaybeAsync(command.UserId, text, null, token);
                return [TelegramTexts.Get(locale, "structured.idea.created", idea.Id, idea.Text)];
            case "note":
                if (text.Length is 0 or > Reference.MaxTextLength) return [TelegramTexts.Get(locale, "structured.note.usage")];
                var note = await service.CreateReferenceAsync(command.UserId, text, null, token);
                return [TelegramTexts.Get(locale, "structured.note.created", note.Id, note.Text)];
            case "wait":
                if (text.Length is 0 or > WaitingFor.MaxDescriptionLength) return [TelegramTexts.Get(locale, "structured.wait.usage")];
                var waiting = await service.CreateWaitingForAsync(command.UserId, text, null, null, token);
                return [TelegramTexts.Get(locale, "structured.wait.created", waiting.Id, waiting.Description)];
            case "list":
                return [TelegramTexts.Get(locale, "structured.list.navigation")];
        }

        if (text.Length > 0)
        {
            return [TelegramTexts.Get(locale, "structured.list.no_arguments", command.Name)];
        }

        return command.Name switch
        {
            "projects" => TelegramTextLists.Render(TelegramTexts.Get(locale, "structured.projects.title"), await service.GetActiveProjectsAsync(command.UserId, token),
                item => $"#{item.Id} {item.Title}", locale),
            "ideas" => TelegramTextLists.Render(TelegramTexts.Get(locale, "structured.ideas.title"), await service.GetActiveSomedayMaybesAsync(command.UserId, token),
                item => $"#{item.Id} {item.Text}", locale),
            "notes" => TelegramTextLists.Render(TelegramTexts.Get(locale, "structured.notes.title"), await service.GetActiveReferencesAsync(command.UserId, token),
                item => $"#{item.Id} {item.Text}", locale),
            "waiting" => TelegramTextLists.Render(TelegramTexts.Get(locale, "structured.waiting.title"), await service.GetActiveWaitingForsAsync(command.UserId, token),
                item => $"#{item.Id} {item.Description}" + (item.Source is null ? "" : TelegramTexts.Get(locale, "structured.waiting.source", item.Source)) +
                    (item.ProjectId is null ? "" : TelegramTexts.Get(locale, "structured.waiting.project", item.ProjectId)), locale),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
    }
}
