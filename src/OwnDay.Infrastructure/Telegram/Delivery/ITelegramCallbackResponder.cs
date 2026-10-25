using Telegram.Bot;

namespace OwnDay.Infrastructure.Telegram.Delivery;

public interface ITelegramCallbackResponder
{
    Task AnswerAsync(string callbackId, CancellationToken cancellationToken);
}

public sealed class TelegramCallbackResponder(ITelegramBotClient bot) : ITelegramCallbackResponder
{
    public Task AnswerAsync(string callbackId, CancellationToken cancellationToken) =>
        bot.AnswerCallbackQuery(callbackId, cancellationToken: cancellationToken);
}
