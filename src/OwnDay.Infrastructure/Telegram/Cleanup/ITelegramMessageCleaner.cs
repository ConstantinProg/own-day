namespace OwnDay.Infrastructure.Telegram.Cleanup;

public interface ITelegramMessageCleaner
{
    Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken);
}
