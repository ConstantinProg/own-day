using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramUpdateHandler
{
    private readonly ITelegramBotClient _botClient;
    private readonly TelegramUpdateRouter _router;

    public TelegramUpdateHandler(ITelegramBotClient botClient, TelegramUpdateRouter router)
    {
        ArgumentNullException.ThrowIfNull(botClient);
        ArgumentNullException.ThrowIfNull(router);
        _botClient = botClient;
        _router = router;
    }

    public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
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
        await _botClient.SendMessage(
            chatId: chatId,
            text: reply.Text,
            cancellationToken: cancellationToken);
    }
}
