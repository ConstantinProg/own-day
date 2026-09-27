namespace OwnDay.Application.Interactions;

public sealed class IncomingCommandHandler : IIncomingCommandHandler
{
    private const string StartResponse =
        "OwnDay is running. Use /help to see available commands.";

    private const string HelpResponse =
        """
        Save: send plain text to add it to your inbox.
        /add <text> — task; /project <text> — project.
        /idea <text> — idea; /note <text> — note; /wait <text> — waiting item.
        Lists: /list, /inbox, /tasks, /projects, /ideas, /notes, /waiting.
        Process: /inbox <id>, /cancel, /discard [id].
        More: /done <id> — complete a task; /start, /help, /ping.
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
