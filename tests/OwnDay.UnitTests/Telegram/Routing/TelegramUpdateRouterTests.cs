using Microsoft.Extensions.Options;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Configuration;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Routing;

public sealed class TelegramUpdateRouterTests
{
    private readonly TelegramUpdateRouter _router = new(
        new TelegramCommandParser(),
        Options.Create(new TelegramOptions { BotUsername = "OwnDayBot" }));

    [Theory]
    [InlineData("/start", "OwnDay is running. Use /help to see available commands.")]
    [InlineData("/ping", "pong")]
    [InlineData("/ping@OwnDayBot", "pong")]
    [InlineData("/unknown", "Unknown command. Use /help.")]
    public void Route_Command_ReturnsExpectedReply(string text, string expected)
    {
        var result = _router.Route(CreateTextMessageUpdate(text));

        var reply = Assert.IsType<TelegramUpdateRouteResult.Reply>(result);
        Assert.Equal(expected, reply.Text);
    }

    [Fact]
    public void Route_HelpCommand_ReturnsHelpReply()
    {
        var result = _router.Route(CreateTextMessageUpdate("/help"));

        var reply = Assert.IsType<TelegramUpdateRouteResult.Reply>(result);
        Assert.Contains("/start", reply.Text);
        Assert.Contains("/help", reply.Text);
        Assert.Contains("/ping", reply.Text);
    }

    [Theory]
    [InlineData("/ping@OtherBot")]
    [InlineData("/unknown@OtherBot")]
    public void Route_CommandAddressedToAnotherBot_ReturnsIgnore(string text)
    {
        var result = _router.Route(CreateTextMessageUpdate(text));

        Assert.IsType<TelegramUpdateRouteResult.Ignore>(result);
    }

    [Fact]
    public void Route_CommandAddressedToThisBotWithDifferentCase_ReturnsReply()
    {
        var result = _router.Route(CreateTextMessageUpdate("/ping@owndaybot"));

        var reply = Assert.IsType<TelegramUpdateRouteResult.Reply>(result);
        Assert.Equal("pong", reply.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    public void Route_NonCommandText_ReturnsIgnore(string text)
    {
        var result = _router.Route(CreateTextMessageUpdate(text));

        Assert.IsType<TelegramUpdateRouteResult.Ignore>(result);
    }

    [Fact]
    public void Route_UpdateWithoutMessage_ReturnsIgnore()
    {
        var result = _router.Route(new Update { Id = 1 });

        Assert.IsType<TelegramUpdateRouteResult.Ignore>(result);
    }

    [Fact]
    public void Route_MessageWithoutText_ReturnsIgnore()
    {
        var update = new Update
        {
            Id = 1,
            Message = new Message
            {
                Id = 1,
                Date = DateTime.UtcNow,
                Chat = CreateChat(ChatType.Private)
            }
        };

        var result = _router.Route(update);

        Assert.IsType<TelegramUpdateRouteResult.Ignore>(result);
    }

    [Theory]
    [InlineData(ChatType.Group)]
    [InlineData(ChatType.Supergroup)]
    [InlineData(ChatType.Channel)]
    public void Route_NonPrivateChat_ReturnsIgnore(ChatType chatType)
    {
        var result = _router.Route(CreateTextMessageUpdate("/ping", chatType));

        Assert.IsType<TelegramUpdateRouteResult.Ignore>(result);
    }

    [Fact]
    public void Route_UnsupportedUpdateType_ReturnsIgnore()
    {
        var update = new Update
        {
            Id = 1,
            CallbackQuery = new CallbackQuery
            {
                Id = "callback-query-id",
                From = new User
                {
                    Id = 1,
                    IsBot = false,
                    FirstName = "Test"
                },
                ChatInstance = "chat-instance"
            }
        };

        var result = _router.Route(update);

        Assert.IsType<TelegramUpdateRouteResult.Ignore>(result);
    }

    private static Update CreateTextMessageUpdate(
        string text,
        ChatType chatType = ChatType.Private) =>
        new()
        {
            Id = 1,
            Message = new Message
            {
                Id = 1,
                Date = DateTime.UtcNow,
                Chat = CreateChat(chatType),
                Text = text
            }
        };

    private static Chat CreateChat(ChatType chatType) =>
        new()
        {
            Id = 1,
            Type = chatType
        };
}
