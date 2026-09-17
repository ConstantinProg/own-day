using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OwnDay.Host.Controllers;
using OwnDay.Infrastructure.Telegram.Handling;
using Telegram.Bot.Types;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Webhook;

public sealed class TelegramWebhookControllerTests
{
    [Fact]
    public async Task Post_Update_ReturnsOk()
    {
        var handler = new RecordingTelegramUpdateHandler();
        var controller = CreateController(handler);

        var result = await controller.Post(CreateUnsupportedUpdate(), CancellationToken.None);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task Post_Update_InvokesHandler()
    {
        var handler = new RecordingTelegramUpdateHandler();
        var controller = CreateController(handler);
        var update = new Update { Id = 42 };

        await controller.Post(update, CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Same(update, call.Update);
    }

    [Fact]
    public async Task Post_UnsupportedValidUpdate_ReturnsOk()
    {
        var handler = new RecordingTelegramUpdateHandler();
        var controller = CreateController(handler);

        var result = await controller.Post(CreateUnsupportedUpdate(), CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Post_PassesCancellationTokenToHandler()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var handler = new RecordingTelegramUpdateHandler();
        var controller = CreateController(handler);

        await controller.Post(CreateUnsupportedUpdate(), cancellationTokenSource.Token);

        var call = Assert.Single(handler.Calls);
        Assert.Equal(cancellationTokenSource.Token, call.CancellationToken);
    }

    private static TelegramWebhookController CreateController(
        ITelegramUpdateHandler handler)
    {
        return new TelegramWebhookController(handler)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static Update CreateUnsupportedUpdate() =>
        new()
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

    private sealed class RecordingTelegramUpdateHandler : ITelegramUpdateHandler
    {
        public List<HandlerCall> Calls { get; } = [];

        public Task HandleAsync(
            Update update,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new HandlerCall(update, cancellationToken));
            return Task.CompletedTask;
        }
    }

    private sealed record HandlerCall(
        Update Update,
        CancellationToken CancellationToken);
}
