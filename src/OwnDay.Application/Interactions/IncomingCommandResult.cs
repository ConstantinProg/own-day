namespace OwnDay.Application.Interactions;

public abstract record IncomingCommandResult
{
    private IncomingCommandResult() { }

    public sealed record Ignore : IncomingCommandResult;

    public sealed record Reply(string Text) : IncomingCommandResult;
}
