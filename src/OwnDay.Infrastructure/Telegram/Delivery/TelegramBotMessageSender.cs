using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

namespace OwnDay.Infrastructure.Telegram.Delivery;

public sealed class TelegramBotMessageSender : ITelegramMessageSender
{
    private readonly ITelegramBotClient _botClient;

    public TelegramBotMessageSender(ITelegramBotClient botClient)
    {
        ArgumentNullException.ThrowIfNull(botClient);

        _botClient = botClient;
    }

    public async Task SendMessageAsync(long chatId, string text, string? replyMarkupJson, CancellationToken cancellationToken)
    {
        using var timeout = DeliveryTimeout(cancellationToken);
        await _botClient.SendMessage(chatId, text,
            replyMarkup: replyMarkupJson is null ? null : JsonSerializer.Deserialize<InlineKeyboardMarkup>(replyMarkupJson),
            cancellationToken: timeout.Token);
    }

    public async Task EditKeyboardAsync(long chatId, int messageId, string? replyMarkupJson, CancellationToken cancellationToken)
    {
        using var timeout = DeliveryTimeout(cancellationToken);
        await _botClient.EditMessageReplyMarkup(chatId, messageId,
            replyMarkup: replyMarkupJson is null ? new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>())
                : JsonSerializer.Deserialize<InlineKeyboardMarkup>(replyMarkupJson),
            cancellationToken: timeout.Token);
    }

    private static CancellationTokenSource DeliveryTimeout(CancellationToken token)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        return timeout;
    }
}
