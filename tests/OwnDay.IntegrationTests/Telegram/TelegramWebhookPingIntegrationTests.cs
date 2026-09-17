using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Infrastructure.Telegram.Delivery;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramWebhookPingIntegrationTests
    : IClassFixture<OwnDayHostFactory>
{
    private const string WebhookPath = "/telegram/webhook";

    private const string SecretHeaderName =
        "X-Telegram-Bot-Api-Secret-Token";

    private const string ValidWebhookSecret =
        "integration-test-webhook-secret";

    private const long ChatId = 123456789;

    private readonly OwnDayHostFactory _factory;

    public TelegramWebhookPingIntegrationTests(
        OwnDayHostFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_PingWithValidSecret_ReturnsOkAndSendsPong()
    {
        var messageSender = new RecordingTelegramMessageSender();

        using var factory = CreateFactory(messageSender);
        using var client = factory.CreateClient();

        using var request = CreatePingRequest(
            ValidWebhookSecret);

        using var response = await client.SendAsync(request);

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        var sentMessage =
            Assert.Single(messageSender.Messages);

        Assert.Equal(
            ChatId,
            sentMessage.ChatId);

        Assert.Equal(
            "pong",
            sentMessage.Text);
    }

    [Fact]
    public async Task Post_PingWithInvalidSecret_ReturnsUnauthorizedAndDoesNotSend()
    {
        var messageSender = new RecordingTelegramMessageSender();

        using var factory = CreateFactory(messageSender);
        using var client = factory.CreateClient();

        using var request = CreatePingRequest(
            "invalid-webhook-secret");

        using var response = await client.SendAsync(request);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            response.StatusCode);

        Assert.Empty(messageSender.Messages);
    }

    [Fact]
    public async Task Post_PingAddressedToAnotherBot_ReturnsOkWithoutSending()
    {
        var messageSender = new RecordingTelegramMessageSender();

        using var factory = CreateFactory(messageSender);
        using var client = factory.CreateClient();

        using var request = CreateRequest(
            ValidWebhookSecret,
            "/ping@OtherBot");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(messageSender.Messages);
    }

    private WebApplicationFactory<Program> CreateFactory(
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

    private static HttpRequestMessage CreatePingRequest(
        string webhookSecret) => CreateRequest(webhookSecret, "/ping");

    private static HttpRequestMessage CreateRequest(
        string webhookSecret,
        string command)
    {
        var json = $$"""
            {
              "update_id": 100001,
              "message": {
                "message_id": 42,
                "date": 1788728400,
                "chat": {
                  "id": {{ChatId}},
                  "type": "private"
                },
                "text": "{{command}}"
              }
            }
            """;

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            WebhookPath);

        request.Headers.TryAddWithoutValidation(
            SecretHeaderName,
            webhookSecret);

        request.Content = new StringContent(
            json,
            Encoding.UTF8,
            "application/json");

        return request;
    }

    private sealed class RecordingTelegramMessageSender
        : ITelegramMessageSender
    {
        public List<SentMessage> Messages { get; } = [];

        public Task SendTextMessageAsync(
            long chatId,
            string text,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(
                new SentMessage(
                    chatId,
                    text,
                    cancellationToken));

            return Task.CompletedTask;
        }
    }

    private sealed record SentMessage(
        long ChatId,
        string Text,
        CancellationToken CancellationToken);
}
