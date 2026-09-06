using OwnDay.Infrastructure.Telegram.Commands;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OwnDay.Infrastructure.Telegram.Routing;

public sealed class TelegramUpdateRouter
{
    private const string StartResponse =
        "OwnDay is running. Use /help to see available commands.";

    private const string HelpResponse =
        """
        Available commands:
        /start — start OwnDay
        /help — show this help
        /ping — check bot availability
        """;

    private const string PingResponse = "pong";
    private const string UnknownCommandResponse = "Unknown command. Use /help.";

    private readonly TelegramCommandParser _commandParser;

    public TelegramUpdateRouter(TelegramCommandParser commandParser)
    {
        ArgumentNullException.ThrowIfNull(commandParser);
        _commandParser = commandParser;
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

        return command.Name switch
        {
            "start" => new TelegramUpdateRouteResult.Reply(StartResponse),
            "help" => new TelegramUpdateRouteResult.Reply(HelpResponse),
            "ping" => new TelegramUpdateRouteResult.Reply(PingResponse),
            _ => new TelegramUpdateRouteResult.Reply(UnknownCommandResponse)
        };
    }
}
