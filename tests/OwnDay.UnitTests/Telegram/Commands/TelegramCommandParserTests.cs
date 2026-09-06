using OwnDay.Infrastructure.Telegram.Commands;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Commands;

public sealed class TelegramCommandParserTests
{
    private readonly TelegramCommandParser _parser = new();

    [Theory]
    [InlineData("/start", "start")]
    [InlineData("/help", "help")]
    [InlineData("/ping", "ping")]
    public void Parse_Command_ReturnsCommand(string text, string expectedName)
    {
        var result = _parser.Parse(text);

        Assert.True(result.IsCommand);
        Assert.NotNull(result.Command);
        Assert.Equal(expectedName, result.Command.Name);
        Assert.Null(result.Command.BotUsername);
        Assert.Empty(result.Command.Arguments);
    }

    [Fact]
    public void Parse_CommandWithBotUsername_ReturnsUsername()
    {
        var result = _parser.Parse("/ping@OwnDayBot");

        Assert.True(result.IsCommand);
        Assert.NotNull(result.Command);
        Assert.Equal("ping", result.Command.Name);
        Assert.Equal("OwnDayBot", result.Command.BotUsername);
        Assert.Empty(result.Command.Arguments);
    }

    [Fact]
    public void Parse_CommandWithArguments_ReturnsArguments()
    {
        var result = _parser.Parse("/start argument");

        Assert.True(result.IsCommand);
        Assert.NotNull(result.Command);
        Assert.Equal("start", result.Command.Name);
        Assert.Null(result.Command.BotUsername);
        Assert.Equal("argument", result.Command.Arguments);
    }

    [Fact]
    public void Parse_CommandWithBotUsernameAndArguments_ReturnsAllParts()
    {
        var result = _parser.Parse("/command@BotName argument1 argument2");

        Assert.True(result.IsCommand);
        Assert.NotNull(result.Command);
        Assert.Equal("command", result.Command.Name);
        Assert.Equal("BotName", result.Command.BotUsername);
        Assert.Equal("argument1 argument2", result.Command.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    public void Parse_NonCommand_ReturnsNotCommand(string? text)
    {
        var result = _parser.Parse(text);

        Assert.False(result.IsCommand);
        Assert.Null(result.Command);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/@OwnDayBot")]
    [InlineData("/ping@")]
    [InlineData("/ping@@OwnDayBot")]
    [InlineData("/ping@Own-DayBot")]
    [InlineData("/ping!")]
    public void Parse_MalformedCommand_ReturnsNotCommand(string text)
    {
        var result = _parser.Parse(text);

        Assert.False(result.IsCommand);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_Command_PreservesCommandCase()
    {
        var result = _parser.Parse("/PING");

        Assert.True(result.IsCommand);
        Assert.NotNull(result.Command);
        Assert.Equal("PING", result.Command.Name);
    }

    [Fact]
    public void Parse_Command_TrimsOuterWhitespace()
    {
        var result = _parser.Parse("  /ping hello world  ");

        Assert.True(result.IsCommand);
        Assert.NotNull(result.Command);
        Assert.Equal("ping", result.Command.Name);
        Assert.Equal("hello world", result.Command.Arguments);
    }

    [Fact]
    public void Parse_Command_AllowsMultipleWhitespaceCharactersBeforeArguments()
    {
        var result = _parser.Parse("/ping   hello world");

        Assert.True(result.IsCommand);
        Assert.NotNull(result.Command);
        Assert.Equal("hello world", result.Command.Arguments);
    }
}
