namespace OwnDay.Infrastructure.Telegram.Delivery;

public interface ITelegramCallbackResponder
{
    Task AnswerAsync(string callbackId, CancellationToken cancellationToken);
}
