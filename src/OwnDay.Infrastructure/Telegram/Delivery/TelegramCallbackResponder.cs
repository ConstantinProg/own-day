using Telegram.Bot;

namespace OwnDay.Infrastructure.Telegram.Delivery;

public sealed class TelegramCallbackResponder(ITelegramBotClient bot) : ITelegramCallbackResponder
{
    public Task AnswerAsync(string callbackId, CancellationToken cancellationToken) =>
        bot.AnswerCallbackQuery(callbackId, cancellationToken: cancellationToken);

    public Task AnswerAsync(string callbackId, string notice, CancellationToken cancellationToken) =>
        bot.AnswerCallbackQuery(callbackId, text: notice, showAlert: true, cancellationToken: cancellationToken);
}
