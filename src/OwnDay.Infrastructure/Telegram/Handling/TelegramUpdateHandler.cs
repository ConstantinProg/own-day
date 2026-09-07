using OwnDay.Infrastructure.Telegram.Delivery;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot.Types;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramUpdateHandler : ITelegramUpdateHandler
{
    private readonly ITelegramMessageSender _messageSender;
    private readonly TelegramUpdateRouter _router;

    public TelegramUpdateHandler(
        ITelegramMessageSender messageSender,
        TelegramUpdateRouter router)
    {
        ArgumentNullException.ThrowIfNull(messageSender);
        ArgumentNullException.ThrowIfNull(router);

        _messageSender = messageSender;
        _router = router;
    }

    public async Task HandleAsync(
        Update update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var result = _router.Route(update);

        if (result is not TelegramUpdateRouteResult.Reply reply)
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
