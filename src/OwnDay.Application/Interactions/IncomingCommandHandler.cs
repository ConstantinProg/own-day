namespace OwnDay.Application.Interactions;

public sealed class IncomingCommandHandler : IIncomingCommandHandler
{
    private const string StartResponse =
        "OwnDay is running. Use /help to see available commands.";

    private const string HelpResponse =
        """
        Сохранить: обычный текст — во входящие.
        /add <текст> — задача; /project <текст> — проект.
        /idea <текст> — идея; /note <текст> — заметка; /wait <текст> — ожидание.
        Списки: /list, /inbox, /tasks, /projects, /ideas, /notes, /waiting.
        Обработка: /inbox <id>, /cancel, /discard [id].
        Дополнительно: /done <id> — завершить задачу; /start, /help, /ping.
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
