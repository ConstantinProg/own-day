namespace OwnDay.Application.Interactions;

public interface IIncomingCommandHandler
{
    Task<IncomingCommandResult> HandleAsync(
        ProcessIncomingCommand command,
        CancellationToken cancellationToken = default);
}
