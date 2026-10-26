using Microsoft.Extensions.Options;
using OwnDay.Application.Interactions;
using OwnDay.Domain;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Configuration;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OwnDay.Infrastructure.Telegram.Routing;

public sealed class TelegramUpdateRouter
{
    private readonly TelegramCommandParser _commandParser;
    private readonly string _botUsername;

    public TelegramUpdateRouter(
        TelegramCommandParser commandParser,
        IOptions<TelegramOptions> options)
    {
        ArgumentNullException.ThrowIfNull(commandParser);
        ArgumentNullException.ThrowIfNull(options);

        _commandParser = commandParser;
        _botUsername = options.Value.BotUsername!;
    }

    public TelegramUpdateRouteResult Route(Update update)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (update.CallbackQuery is { From.IsBot: false, Message.Chat.Type: ChatType.Private, Data: { } navigation } nav &&
            nav.Message.Chat.Id == nav.From.Id && navigation.StartsWith("nav:", StringComparison.Ordinal) &&
            navigation[4..] is "list" or "inbox" or "tasks" or "projects" or "ideas" or "notes" or "waiting" or "settings" or "help")
        {
            return new TelegramUpdateRouteResult.Dispatch(new ProcessIncomingCommand(
                navigation[4..], "", new UserId(nav.From.Id)), nav.Message.Chat.Id);
        }

        if (update.CallbackQuery is { From.IsBot: false, Message.Chat.Type: ChatType.Private, Data: { } detail } resultCallback &&
            resultCallback.Message.Chat.Id == resultCallback.From.Id && detail.StartsWith("ir:", StringComparison.Ordinal) &&
            long.TryParse(detail.AsSpan(3), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var resultId) && resultId > 0)
        {
            return new TelegramUpdateRouteResult.Dispatch(new ProcessIncomingCommand(
                "inbox_result", resultId.ToString(System.Globalization.CultureInfo.InvariantCulture), new UserId(resultCallback.From.Id)),
                resultCallback.Message.Chat.Id);
        }

        if (update.CallbackQuery is { From.IsBot: false, Message.Chat.Type: ChatType.Private, Data: { } data } callback &&
            callback.Message.Chat.Id == callback.From.Id && data.Length == 37 &&
            (data.StartsWith("ia:y:", StringComparison.Ordinal) || data.StartsWith("ia:n:", StringComparison.Ordinal)) &&
            Guid.TryParseExact(data[5..], "N", out var analysisId))
        {
            return new TelegramUpdateRouteResult.Dispatch(new ProcessIncomingCommand(
                data[3] == 'y' ? "inbox_accept" : "inbox_reject", analysisId.ToString("N"), new UserId(callback.From.Id)),
                callback.Message.Chat.Id);
        }

        if (update.CallbackQuery is { From.IsBot: false, Message.Chat.Type: ChatType.Private, Data: { } undo } undoCallback &&
            undoCallback.Message.Chat.Id == undoCallback.From.Id && undo.StartsWith("iu:", StringComparison.Ordinal) &&
            long.TryParse(undo.AsSpan(3), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var itemId) && itemId > 0)
        {
            return new TelegramUpdateRouteResult.Dispatch(new ProcessIncomingCommand(
                "inbox_undo", itemId.ToString(System.Globalization.CultureInfo.InvariantCulture), new UserId(undoCallback.From.Id)),
                undoCallback.Message.Chat.Id);
        }

        var message = update.Message;

        if (message is null ||
            message.Chat.Type is not ChatType.Private ||
            message.From is null ||
            message.From.IsBot ||
            message.Text is null)
        {
            return new TelegramUpdateRouteResult.Ignore();
        }

        var parseResult = _commandParser.Parse(message.Text);

        if (!parseResult.IsCommand)
        {
            return message.Text.AsSpan().TrimStart().StartsWith('/') || string.IsNullOrWhiteSpace(message.Text)
                ? new TelegramUpdateRouteResult.Ignore()
                : new TelegramUpdateRouteResult.Text(new UserId(message.From.Id), message.Chat.Id, message.Text);
        }

        var command = parseResult.Command!;

        if (command.BotUsername is not null &&
            !string.Equals(
                command.BotUsername,
                _botUsername,
                StringComparison.OrdinalIgnoreCase))
        {
            return new TelegramUpdateRouteResult.Ignore();
        }

        return new TelegramUpdateRouteResult.Dispatch(
            new ProcessIncomingCommand(command.Name switch
            {
                "result" => "inbox_result",
                "undo" => "inbox_undo",
                _ => command.Name
            }, command.Arguments, new UserId(message.From.Id)),
            message.Chat.Id);
    }
}
