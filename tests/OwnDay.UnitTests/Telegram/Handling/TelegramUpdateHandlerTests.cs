using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Handling;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Handling;

public sealed class TelegramUpdateHandlerTests
{
    [Fact]
    public async Task HandleAsync_Ignore_DoesNotSendTelegramRequest()
    {
        var botClient = new FakeTelegramBotClient();
        var handler = CreateHandler(botClient);

        await handler.HandleAsync(CreateTextMessageUpdate("hello"));

        Assert.Empty(botClient.Requests);
    }

    [Fact]
    public async Task HandleAsync_Reply_SendsMessageOnce()
    {
        var botClient = new FakeTelegramBotClient();
        var handler = CreateHandler(botClient);

        await handler.HandleAsync(CreateTextMessageUpdate("/ping"));

        Assert.Single(botClient.Requests);
    }

    [Fact]
    public async Task HandleAsync_Reply_SendsToOriginalChat()
    {
        const long chatId = 123456789;

        var botClient = new FakeTelegramBotClient();
        var handler = CreateHandler(botClient);

        await handler.HandleAsync(
            CreateTextMessageUpdate("/ping", chatId));

        var request = Assert.IsType<SendMessageRequest>(
            Assert.Single(botClient.Requests).Request);

        Assert.Equal(new ChatId(chatId), request.ChatId);
    }

    [Fact]
    public async Task HandleAsync_Reply_SendsExpectedText()
    {
        var botClient = new FakeTelegramBotClient();
        var handler = CreateHandler(botClient);

        await handler.HandleAsync(
            CreateTextMessageUpdate("/ping"));

        var request = Assert.IsType<SendMessageRequest>(
            Assert.Single(botClient.Requests).Request);

        Assert.Equal("pong", request.Text);
    }

    [Fact]
    public async Task HandleAsync_Reply_PassesCancellationToken()
    {
        using var cancellationTokenSource =
            new CancellationTokenSource();

        var botClient = new FakeTelegramBotClient();
        var handler = CreateHandler(botClient);

        await handler.HandleAsync(
            CreateTextMessageUpdate("/ping"),
            cancellationTokenSource.Token);

        var sentRequest = Assert.Single(botClient.Requests);

        Assert.Equal(
            cancellationTokenSource.Token,
            sentRequest.CancellationToken);
    }

    private static TelegramUpdateHandler CreateHandler(
        ITelegramBotClient botClient)
    {
        var parser = new TelegramCommandParser();
        var router = new TelegramUpdateRouter(parser);

        return new TelegramUpdateHandler(
            botClient,
            router);
    }

    private static Update CreateTextMessageUpdate(
        string text,
        long chatId = 1)
    {
        return new Update
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
    }

    private sealed class FakeTelegramBotClient
        : ITelegramBotClient
    {
        public List<SentRequest> Requests { get; } = [];

        public bool LocalBotServer => false;

        public long BotId => 1;

        public TimeSpan Timeout { get; set; }

        public IExceptionParser ExceptionsParser { get; set; } =
            new DefaultExceptionParser();

        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest
        {
            add
            {
            }
            remove
            {
            }
        }

        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived
        {
            add
            {
            }
            remove
            {
            }
        }

        public Task<TResponse> SendRequest<TResponse>(
            IRequest<TResponse> request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(
                new SentRequest(
                    request,
                    cancellationToken));

            return Task.FromResult(
                CreateResponse<TResponse>());
        }

        public Task<bool> TestApi(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task DownloadFile(
            string filePath,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DownloadFile(
            TGFile file,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        private static TResponse CreateResponse<TResponse>()
        {
            if (typeof(TResponse) == typeof(Message))
            {
                return (TResponse)(object)new Message
                {
                    Id = 1,
                    Date = DateTime.UtcNow,
                    Chat = new Chat
                    {
                        Id = 1,
                        Type = ChatType.Private
                    }
                };
            }

            return default!;
        }
    }

    private sealed record SentRequest(
        object Request,
        CancellationToken CancellationToken);
}