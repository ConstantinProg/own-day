using System.Globalization;
using System.Text;
using OwnDay.Application.Actions;
using OwnDay.Application.Interactions;
using OwnDay.Infrastructure.Telegram.Localization;
using Action = OwnDay.Domain.Actions.Action;

namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed class TelegramActionCommandHandler(ActionService actionService)
{
    private const int MaximumMessageLength = 4096;

    public bool Handles(string name) => name is "add" or "tasks" or "done";

    public async Task<IReadOnlyList<string>> HandleAsync(
        ProcessIncomingCommand command,
        string locale,
        CancellationToken cancellationToken)
    {
        if (command.Name is "tasks")
        {
            return await ListAsync(command, locale, cancellationToken);
        }

        return [await HandleSingleAsync(command, locale, cancellationToken)];
    }

    private Task<string> HandleSingleAsync(ProcessIncomingCommand command, string locale, CancellationToken cancellationToken) =>
        command.Name switch
        {
            "add" => AddAsync(command, locale, cancellationToken),
            "done" => CompleteAsync(command, locale, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };

    private async Task<string> AddAsync(ProcessIncomingCommand command, string locale, CancellationToken cancellationToken)
    {
        var title = command.Arguments.Trim();
        if (title.Length is 0)
        {
            return TelegramTexts.Get(locale, "action.add.usage");
        }

        if (title.Length > Action.MaxTitleLength)
        {
            return TelegramTexts.Get(locale, "action.add.too_long", Action.MaxTitleLength);
        }

        var action = await actionService.AddAsync(command.UserId, title, cancellationToken);
        return TelegramTexts.Get(locale, "action.add.created", action.Id, action.Title);
    }

    private async Task<IReadOnlyList<string>> ListAsync(
        ProcessIncomingCommand command,
        string locale,
        CancellationToken cancellationToken)
    {
        if (command.Arguments.Length > 0)
        {
            return [TelegramTexts.Get(locale, "action.list.usage")];
        }

        var actions = await actionService.GetActiveAsync(command.UserId, cancellationToken);
        if (actions.Count is 0)
        {
            return [TelegramTexts.Get(locale, "action.list.empty")];
        }

        var messages = new List<string>();
        var current = new StringBuilder();
        foreach (var action in actions)
        {
            var line = $"#{action.Id} {action.Title}";
            if (current.Length > 0 && current.Length + 1 + line.Length > MaximumMessageLength)
            {
                messages.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(line);
        }

        messages.Add(current.ToString());
        return messages;
    }

    private async Task<string> CompleteAsync(ProcessIncomingCommand command, string locale, CancellationToken cancellationToken)
    {
        if (!long.TryParse(command.Arguments, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return TelegramTexts.Get(locale, "action.done.usage");
        }

        var result = await actionService.CompleteAsync(command.UserId, id, cancellationToken);
        return result switch
        {
            CompleteActionResult.Completed => TelegramTexts.Get(locale, "action.done.completed", id),
            CompleteActionResult.AlreadyCompleted => TelegramTexts.Get(locale, "action.done.already", id),
            _ => TelegramTexts.Get(locale, "action.done.not_found")
        };
    }
}
