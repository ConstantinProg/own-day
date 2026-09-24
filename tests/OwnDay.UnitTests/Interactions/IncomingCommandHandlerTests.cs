using OwnDay.Application.Interactions;
using Xunit;

namespace OwnDay.UnitTests.Interactions;

public sealed class IncomingCommandHandlerTests
{
    private readonly IncomingCommandHandler _handler = new();

    [Theory]
    [InlineData("start", "OwnDay is running. Use /help to see available commands.")]
    [InlineData("ping", "pong")]
    [InlineData("unknown", "Unknown command. Use /help.")]
    public async Task HandleAsync_Command_ReturnsExpectedReply(
        string commandName,
        string expectedText)
    {
        var result = await _handler.HandleAsync(
            new ProcessIncomingCommand(commandName, string.Empty));

        var reply = Assert.IsType<IncomingCommandResult.Reply>(result);
        Assert.Equal(expectedText, reply.Text);
    }

    [Fact]
    public async Task HandleAsync_HelpCommand_ReturnsAvailableCommands()
    {
        var result = await _handler.HandleAsync(
            new ProcessIncomingCommand("help", string.Empty));

        var reply = Assert.IsType<IncomingCommandResult.Reply>(result);
        Assert.Contains("/start", reply.Text);
        Assert.Contains("/help", reply.Text);
        Assert.Contains("/ping", reply.Text);
        Assert.Contains("/add", reply.Text);
        Assert.Contains("/tasks", reply.Text);
        Assert.Contains("/done", reply.Text);
    }

    [Fact]
    public async Task HandleAsync_CanceledToken_ThrowsOperationCanceledException()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _handler.HandleAsync(
                new ProcessIncomingCommand("ping", string.Empty),
                cancellationTokenSource.Token));
    }
}
