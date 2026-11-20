namespace OwnDay.Infrastructure.Telegram.Delivery;

public interface ITelegramCallbackResponder
{
    Task AnswerAsync(string callbackId, CancellationToken cancellationToken);

    Task AnswerAsync(string callbackId, string notice, CancellationToken cancellationToken) =>
        AnswerAsync(callbackId, cancellationToken);
}
