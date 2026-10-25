namespace OwnDay.Infrastructure.Telegram.Delivery;

public interface ITelegramMessageSender
{
    Task SendMessageAsync(
        long chatId,
        string text,
        string? replyMarkupJson,
        CancellationToken cancellationToken = default);
}
