using Microsoft.Extensions.Options;
using OwnDay.Application.Interactions;
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

        var message = update.Message;

        if (message is null ||
            message.Chat.Type is not ChatType.Private ||
            message.Text is null)
        {
            return new TelegramUpdateRouteResult.Ignore();
        }

        var parseResult = _commandParser.Parse(message.Text);

        if (!parseResult.IsCommand)
        {
            return new TelegramUpdateRouteResult.Ignore();
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
            new ProcessIncomingCommand(command.Name, command.Arguments), message.Chat.Id);
    }
}
