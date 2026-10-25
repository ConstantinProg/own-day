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
        await _botClient.SendMessage(chatId, text,
            replyMarkup: replyMarkupJson is null ? null : JsonSerializer.Deserialize<InlineKeyboardMarkup>(replyMarkupJson),
            cancellationToken: cancellationToken);
    }
}
