using OwnDay.Application.Interactions;
using Microsoft.Extensions.Options;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Delivery;
using OwnDay.Infrastructure.Telegram.Handling;
using OwnDay.Infrastructure.Telegram.Routing;
using OwnDay.Infrastructure.Telegram.Configuration;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Handling;

public sealed class TelegramUpdateHandlerTests
{
    [Fact]
    public async Task HandleAsync_Ignore_DoesNotSendMessage()
    {
        var messageSender = new RecordingTelegramMessageSender();
        var handler = CreateHandler(messageSender);

        await handler.HandleAsync(CreateTextMessageUpdate("hello"));

        Assert.Empty(messageSender.Messages);
    }

    [Fact]
    public async Task HandleAsync_Reply_SendsExactlyOnce()
    {
        var messageSender = new RecordingTelegramMessageSender();
        var handler = CreateHandler(messageSender);

        await handler.HandleAsync(CreateTextMessageUpdate("/ping"));

        Assert.Single(messageSender.Messages);
    }

    [Fact]
    public async Task HandleAsync_Reply_SendsToOriginalChat()
    {
        const long chatId = 123456789;

        var messageSender = new RecordingTelegramMessageSender();
        var handler = CreateHandler(messageSender);

        await handler.HandleAsync(CreateTextMessageUpdate("/ping", chatId));

        var message = Assert.Single(messageSender.Messages);
        Assert.Equal(chatId, message.ChatId);
    }

    [Fact]
    public async Task HandleAsync_Reply_SendsExpectedText()
    {
        var messageSender = new RecordingTelegramMessageSender();
        var handler = CreateHandler(messageSender);

        await handler.HandleAsync(CreateTextMessageUpdate("/ping"));

        var message = Assert.Single(messageSender.Messages);
        Assert.Equal("pong", message.Text);
    }

    [Fact]
    public async Task HandleAsync_Reply_PassesCancellationToken()
    {
        using var cancellationTokenSource = new CancellationTokenSource();

        var messageSender = new RecordingTelegramMessageSender();
        var handler = CreateHandler(messageSender);

        await handler.HandleAsync(
            CreateTextMessageUpdate("/ping"),
            cancellationTokenSource.Token);

        var message = Assert.Single(messageSender.Messages);
        Assert.Equal(cancellationTokenSource.Token, message.CancellationToken);
    }

    private static TelegramUpdateHandler CreateHandler(
        ITelegramMessageSender messageSender)
    {
        var parser = new TelegramCommandParser();
        var router = new TelegramUpdateRouter(
            parser,
            Options.Create(new TelegramOptions { BotUsername = "OwnDayBot" }));

        return new TelegramUpdateHandler(
            messageSender,
            router,
            new IncomingCommandHandler());
    }

    private static Update CreateTextMessageUpdate(
        string text,
        long chatId = 1) =>
        new()
        {
            Id = 1,
            Message = new Message
            {
                Id = 1,
                Date = DateTime.UtcNow,
                Chat = new Chat
                {
                    Id = chatId,
                    Type = ChatType.Private
                },
                Text = text
            }
        };

    private sealed class RecordingTelegramMessageSender : ITelegramMessageSender
    {
        public List<SentMessage> Messages { get; } = [];

        public Task SendTextMessageAsync(
            long chatId,
            string text,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(new SentMessage(chatId, text, cancellationToken));
            return Task.CompletedTask;
        }
    }

    private sealed record SentMessage(
        long ChatId,
        string Text,
        CancellationToken CancellationToken);
}
