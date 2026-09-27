using OwnDay.Application.Interactions;
using OwnDay.Domain;

namespace OwnDay.Infrastructure.Telegram.Routing;

public abstract record TelegramUpdateRouteResult
{
    private TelegramUpdateRouteResult() { }

    public sealed record Ignore : TelegramUpdateRouteResult;

    public sealed record Dispatch(ProcessIncomingCommand Command, long ChatId) : TelegramUpdateRouteResult;

    public sealed record Text(UserId UserId, long ChatId, string Value) : TelegramUpdateRouteResult;
}
