namespace OwnDay.Application.Interactions;

public sealed class IncomingCommandHandler : IIncomingCommandHandler
{
    private const string StartResponse =
        "OwnDay is running. Use /help to see available commands.";

    private const string HelpResponse =
        """
        Available commands:
        /start — start OwnDay
        /help — show this help
        /ping — check bot availability
        /add <title> — save a task
        /tasks — list active tasks
        /done <id> — complete a task
        """;

    private const string PingResponse = "pong";
    private const string UnknownCommandResponse = "Unknown command. Use /help.";

    public Task<IncomingCommandResult> HandleAsync(
        ProcessIncomingCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        IncomingCommandResult result = command.Name switch
        {
            "start" => new IncomingCommandResult.Reply(StartResponse),
            "help" => new IncomingCommandResult.Reply(HelpResponse),
            "ping" => new IncomingCommandResult.Reply(PingResponse),
            _ => new IncomingCommandResult.Reply(UnknownCommandResponse)
        };

        return Task.FromResult(result);
    }
}
