using Telegram.Bot;

namespace OwnDay.Infrastructure.Telegram.Cleanup;

public sealed class TelegramBotMessageCleaner : ITelegramMessageCleaner
{
    private readonly ITelegramBotClient _botClient;

    public TelegramBotMessageCleaner(ITelegramBotClient botClient)
    {
        ArgumentNullException.ThrowIfNull(botClient);

        _botClient = botClient;
    }

    public async Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken)
    {
        await _botClient.DeleteMessage(chatId: chatId, messageId: messageId, cancellationToken: cancellationToken);
    }
}
