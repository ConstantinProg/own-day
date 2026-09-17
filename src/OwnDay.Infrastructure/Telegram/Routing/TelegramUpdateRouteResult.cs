using OwnDay.Application.Interactions;

namespace OwnDay.Infrastructure.Telegram.Routing;

public abstract record TelegramUpdateRouteResult
{
    private TelegramUpdateRouteResult() { }

    public sealed record Ignore : TelegramUpdateRouteResult;

    public sealed record Dispatch(ProcessIncomingCommand Command) : TelegramUpdateRouteResult;
}
