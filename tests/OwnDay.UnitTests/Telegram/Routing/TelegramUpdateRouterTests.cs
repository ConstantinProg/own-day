using Microsoft.Extensions.Options;
using OwnDay.Application.Interactions;
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
    [InlineData(true, "inbox_accept")]
    [InlineData(false, "inbox_reject")]
    public void Route_InboxCallback_UsesAuthenticatedSenderAndStableAnalysisIdentity(bool accept, string expected)
    {
        var id = Guid.NewGuid();
        var update = CreateCallback($"ia:{(accept ? "y" : "n")}:{id:N}");
        var result = Assert.IsType<TelegramUpdateRouteResult.Dispatch>(_router.Route(update));
        Assert.Equal(expected, result.Command.Name);
        Assert.Equal(id.ToString("N"), result.Command.Arguments);
        Assert.Equal(new(1), result.Command.UserId);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("group")]
    [InlineData("bot")]
    [InlineData("foreign-chat")]
    [InlineData("missing-message")]
    public void Route_UnsupportedOrUntrustedCallback_IgnoresUpdate(string scenario)
    {
        var update = CreateCallback($"ia:y:{Guid.NewGuid():N}");
        var callback = update.CallbackQuery!;
        switch (scenario)
        {
            case "malformed": callback.Data = "ia:y:not-a-guid"; break;
            case "group": callback.Message!.Chat.Type = ChatType.Group; break;
            case "bot": callback.From.IsBot = true; break;
            case "foreign-chat": callback.Message!.Chat.Id = 2; break;
            case "missing-message": callback.Message = null; break;
        }
        Assert.IsType<TelegramUpdateRouteResult.Ignore>(_router.Route(update));
    }

    private static Update CreateCallback(string data) => new()
    {
        Id = 1,
        CallbackQuery = new()
        {
            Id = "callback", ChatInstance = "private-chat", Data = data,
            From = new() { Id = 1, FirstName = "Test" },
            Message = new() { Id = 10, Date = DateTime.UtcNow, Chat = new() { Id = 1, Type = ChatType.Private } }
        }
    };

    [Theory]
    [InlineData("/start", "start")]
    [InlineData("/help", "help")]
    [InlineData("/ping", "ping")]
    [InlineData("/ping@OwnDayBot", "ping")]
    [InlineData("/unknown", "unknown")]
    public void Route_Command_ReturnsApplicationCommand(string text, string expectedName)
    {
        var result = _router.Route(CreateTextMessageUpdate(text));

        var dispatch = Assert.IsType<TelegramUpdateRouteResult.Dispatch>(result);
        Assert.Equal(expectedName, dispatch.Command.Name);
        Assert.Empty(dispatch.Command.Arguments);
    }

    [Fact]
    public void Route_Command_PreservesChatId()
    {
        var update = CreateTextMessageUpdate("/ping");
        update.Message!.Chat.Id = 123456789;

        var dispatch = Assert.IsType<TelegramUpdateRouteResult.Dispatch>(_router.Route(update));

        Assert.Equal(123456789, dispatch.ChatId);
    }

    [Fact]
    public void Route_Command_UsesTelegramSenderAsUserId()
    {
        var update = CreateTextMessageUpdate("/tasks");
        update.Message!.Chat.Id = 123456789;
        update.Message.From!.Id = 987654321;

        var dispatch = Assert.IsType<TelegramUpdateRouteResult.Dispatch>(_router.Route(update));

        Assert.Equal(123456789, dispatch.ChatId);
        Assert.Equal(new(987654321), dispatch.Command.UserId);
    }

    [Fact]
    public void Route_MessageWithoutSender_ReturnsIgnore()
    {
        var update = CreateTextMessageUpdate("/tasks");
        update.Message!.From = null;

        Assert.IsType<TelegramUpdateRouteResult.Ignore>(_router.Route(update));
    }

    [Fact]
    public void Route_CommandWithArguments_PreservesArguments()
    {
        var result = _router.Route(CreateTextMessageUpdate("/start first step"));

        var dispatch = Assert.IsType<TelegramUpdateRouteResult.Dispatch>(result);
        Assert.Equal(new ProcessIncomingCommand("start", "first step", new(1)), dispatch.Command);
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
    public void Route_CommandAddressedToThisBotWithDifferentCase_ReturnsApplicationCommand()
    {
        var result = _router.Route(CreateTextMessageUpdate("/ping@owndaybot"));

        var dispatch = Assert.IsType<TelegramUpdateRouteResult.Dispatch>(result);
        Assert.Equal("ping", dispatch.Command.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Route_NonCommandText_ReturnsIgnore(string text)
    {
        var result = _router.Route(CreateTextMessageUpdate(text));

        Assert.IsType<TelegramUpdateRouteResult.Ignore>(result);
    }

    [Fact]
    public void Route_OrdinaryText_ReturnsText()
    {
        var result = Assert.IsType<TelegramUpdateRouteResult.Text>(_router.Route(CreateTextMessageUpdate("hello")));
        Assert.Equal("hello", result.Value);
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
                From = new User { Id = 1, IsBot = false, FirstName = "Test" },
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
                From = new User { Id = 1, IsBot = false, FirstName = "Test" },
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
