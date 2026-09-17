using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Infrastructure.Telegram.Delivery;
using OwnDay.Infrastructure.Telegram.Handling;
using Telegram.Bot.Types;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramWebhookEndpointTests : IClassFixture<OwnDayHostFactory>
{
    private const string WebhookSecret = "integration-test-webhook-secret";
    private const string SecretHeaderName = "X-Telegram-Bot-Api-Secret-Token";

    private readonly OwnDayHostFactory _factory;

    public TelegramWebhookEndpointTests(OwnDayHostFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_WithoutSecret_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/telegram/webhook",
            new Update { Id = 1 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithInvalidSecret_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(SecretHeaderName, "invalid-secret");

        using var response = await client.PostAsJsonAsync(
            "/telegram/webhook",
            new Update { Id = 1 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithValidSecret_ReturnsOk()
    {
        var handler = new RecordingTelegramUpdateHandler();

        using var factory = CreateFactoryWithHandler(handler);
        using var client = factory.CreateClient();
        AddValidSecret(client);

        using var response = await client.PostAsJsonAsync(
            "/telegram/webhook",
            new Update { Id = 42 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithValidSecret_DelegatesUpdateExactlyOnce()
    {
        var handler = new RecordingTelegramUpdateHandler();

        using var factory = CreateFactoryWithHandler(handler);
        using var client = factory.CreateClient();
        AddValidSecret(client);

        using var response = await client.PostAsJsonAsync(
            "/telegram/webhook",
            new Update { Id = 42 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var call = Assert.Single(handler.Calls);
        Assert.Equal(42, call.Update.Id);
    }

    [Fact]
    public async Task Post_UnsupportedUpdate_ReturnsOkWithoutSendingMessage()
    {
        var messageSender = new RecordingTelegramMessageSender();

        using var factory = CreateFactoryWithMessageSender(messageSender);
        using var client = factory.CreateClient();
        AddValidSecret(client);

        using var response = await client.PostAsJsonAsync(
            "/telegram/webhook",
            new Update { Id = 100 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(messageSender.Messages);
    }

    [Theory]
    [InlineData(null, "{")]
    [InlineData("invalid-secret", "{")]
    [InlineData("", "{")]
    [InlineData(null, "")]
    public async Task Post_UnauthorizedMalformedBody_ReturnsUnauthorizedWithoutDispatch(
        string? secret, string body)
    {
        var handler = new RecordingTelegramUpdateHandler();
        using var factory = CreateFactoryWithHandler(handler);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        if (secret is not null)
        {
            request.Headers.TryAddWithoutValidation(SecretHeaderName, secret);
        }

        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"update_id\":42}")]
    public async Task Post_MultipleSecretValues_ReturnsUnauthorizedWithoutDispatch(string body)
    {
        var handler = new RecordingTelegramUpdateHandler();
        using var factory = CreateFactoryWithHandler(handler);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.TryAddWithoutValidation(SecretHeaderName, new[] { WebhookSecret, WebhookSecret });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Post_AuthorizedMalformedBody_ReturnsBadRequestWithoutDispatch()
    {
        var handler = new RecordingTelegramUpdateHandler();
        using var factory = CreateFactoryWithHandler(handler);
        using var client = factory.CreateClient();
        AddValidSecret(client);
        using var content = new StringContent("{", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/telegram/webhook", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(handler.Calls);
    }

    private WebApplicationFactory<Program> CreateFactoryWithHandler(
        ITelegramUpdateHandler handler)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITelegramUpdateHandler>();
                services.AddSingleton(handler);
            });
        });
    }

    private WebApplicationFactory<Program> CreateFactoryWithMessageSender(
        ITelegramMessageSender messageSender)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITelegramMessageSender>();
                services.AddSingleton(messageSender);
            });
        });
    }

    private static void AddValidSecret(HttpClient client)
    {
        client.DefaultRequestHeaders.Add(SecretHeaderName, WebhookSecret);
    }

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

    private sealed record HandlerCall(
        Update Update,
        CancellationToken CancellationToken);

    private sealed record SentMessage(
        long ChatId,
        string Text,
        CancellationToken CancellationToken);
}
