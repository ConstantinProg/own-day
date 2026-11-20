using OwnDay.Application.Interactions;
using OwnDay.Application.Learning;
using OwnDay.Application.TesterMode;
using OwnDay.Domain;

namespace OwnDay.Infrastructure.Telegram.Routing;

public abstract record TelegramUpdateRouteResult
{
    private TelegramUpdateRouteResult() { }

    public sealed record Ignore : TelegramUpdateRouteResult;

    public sealed record Dispatch(ProcessIncomingCommand Command, long ChatId) : TelegramUpdateRouteResult;

    public sealed record Text(UserId UserId, long ChatId, string Value) : TelegramUpdateRouteResult;

    public sealed record Action(UserId UserId, long ChatId, LearningAction Value, DateTimeOffset ExpiresAt) : TelegramUpdateRouteResult;

    public sealed record TesterAction(UserId UserId, TesterModeConfirmation Confirmation) : TelegramUpdateRouteResult;

    public sealed record UnavailableAction(bool InRussian) : TelegramUpdateRouteResult;
}
