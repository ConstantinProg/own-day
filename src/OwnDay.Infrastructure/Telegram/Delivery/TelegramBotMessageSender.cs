using Telegram.Bot;

namespace OwnDay.Infrastructure.Telegram.Delivery;

public sealed class TelegramBotMessageSender : ITelegramMessageSender
{
    private readonly ITelegramBotClient _botClient;

    public TelegramBotMessageSender(ITelegramBotClient botClient)
    {
        ArgumentNullException.ThrowIfNull(botClient);

        _botClient = botClient;
    }

    public async Task SendTextMessageAsync(
        long chatId,
        string text,
        CancellationToken cancellationToken = default)
    {
        await _botClient.SendMessage(
            chatId: chatId,
            text: text,
            cancellationToken: cancellationToken);
    }
}
