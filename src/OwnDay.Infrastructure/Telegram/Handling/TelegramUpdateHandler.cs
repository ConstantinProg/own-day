using OwnDay.Application.Interactions;
using OwnDay.Infrastructure.Telegram.Delivery;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot.Types;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramUpdateHandler : ITelegramUpdateHandler
{
    private readonly ITelegramMessageSender _messageSender;
    private readonly IIncomingCommandHandler _commandHandler;
    private readonly TelegramUpdateRouter _router;

    public TelegramUpdateHandler(
        ITelegramMessageSender messageSender,
        TelegramUpdateRouter router,
        IIncomingCommandHandler commandHandler)
    {
        ArgumentNullException.ThrowIfNull(messageSender);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(commandHandler);

        _messageSender = messageSender;
        _router = router;
        _commandHandler = commandHandler;
    }

    public async Task HandleAsync(
        Update update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var result = _router.Route(update);

        if (result is not TelegramUpdateRouteResult.Dispatch dispatch)
        {
            return;
        }

        var commandResult = await _commandHandler.HandleAsync(
            dispatch.Command,
            cancellationToken);

        if (commandResult is not IncomingCommandResult.Reply reply)
        {
            return;
        }

        var chatId = update.Message!.Chat.Id;

        // Direct Telegram delivery is temporary for side-effect-free Phase 1
        // commands. Domain-changing flows must use transactional outbox.
        await _messageSender.SendTextMessageAsync(
            chatId,
            reply.Text,
            cancellationToken);
    }
}
