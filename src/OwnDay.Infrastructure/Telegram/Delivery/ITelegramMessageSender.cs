namespace OwnDay.Infrastructure.Telegram.Delivery;

public interface ITelegramMessageSender
{
    Task SendTextMessageAsync(
        long chatId,
        string text,
        CancellationToken cancellationToken = default);
}
