using Telegram.Bot.Types;

namespace OwnDay.Infrastructure.Telegram.Handling;

public interface ITelegramUpdateHandler
{
    Task HandleAsync(
        Update update,
        CancellationToken cancellationToken = default);
}
