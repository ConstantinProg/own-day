using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Cleanup;
using OwnDay.Infrastructure.Telegram.Delivery;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramWebhookPingIntegrationTests
{
    private const string WebhookPath = "/telegram/webhook";

    private const string SecretHeaderName =
        "X-Telegram-Bot-Api-Secret-Token";

    private const string ValidWebhookSecret =
        "integration-test-webhook-secret";

    private const long ChatId = 123456789;

    [Fact]
    public async Task Post_PingWithValidSecret_QueuesPongWithoutDirectDelivery()
    {
        var messageSender = new RecordingTelegramMessageSender();

        using var host = new OwnDayHostFactory();
        using var factory = CreateFactory(host, messageSender);
        using var client = factory.CreateClient();

        using var request = CreatePingRequest(
            ValidWebhookSecret);

        using var response = await client.SendAsync(request);

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        Assert.Empty(messageSender.Messages);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var reply = Assert.Single(await dbContext.TelegramOutboxMessages.ToListAsync());
        Assert.Equal(ChatId, reply.ChatId);
        Assert.Equal("pong", reply.Text);
        Assert.Equal(OutboxMessageStatus.Pending, reply.Status);
        var processed = Assert.Single(await dbContext.ProcessedTelegramUpdates.ToListAsync());
        Assert.Equal(100001, processed.UpdateId);
        Assert.NotNull(processed.ProcessedAt);
    }

    [Fact]
    public async Task Post_PrivateText_CapturesAndRequestsMessageDeletion()
    {
        var cleaner = new RecordingTelegramMessageCleaner();
        using var host = new OwnDayHostFactory();
        using var factory = CreateFactory(host, new RecordingTelegramMessageSender(), cleaner);
        using var client = factory.CreateClient();
        using var request = CreateRequest(ValidWebhookSecret, "buy milk");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((ChatId, 42), Assert.Single(cleaner.Deletions));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Single(await db.InboxItems.ToListAsync());
        Assert.Single(await db.TelegramOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Post_DeletionFails_ReturnsOkWithReplyStillQueued()
    {
        var cleaner = new RecordingTelegramMessageCleaner
        {
            Failure = new InvalidOperationException("Telegram rejected deletion")
        };
        using var host = new OwnDayHostFactory();
        using var factory = CreateFactory(host, new RecordingTelegramMessageSender(), cleaner);
        using var client = factory.CreateClient();
        using var request = CreateRequest(ValidWebhookSecret, "buy milk");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((ChatId, 42), Assert.Single(cleaner.Deletions));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Single(await db.InboxItems.ToListAsync());
        Assert.Equal(OutboxMessageStatus.Pending, Assert.Single(await db.TelegramOutboxMessages.ToListAsync()).Status);
    }

    [Fact]
    public async Task Post_PingWithInvalidSecret_ReturnsUnauthorizedAndDoesNotSend()
    {
        var messageSender = new RecordingTelegramMessageSender();

        using var host = new OwnDayHostFactory();
        using var factory = CreateFactory(host, messageSender);
        using var client = factory.CreateClient();

        using var request = CreatePingRequest(
            "invalid-webhook-secret");

        using var response = await client.SendAsync(request);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            response.StatusCode);

        Assert.Empty(messageSender.Messages);
        await AssertNoPersistedMessagesAsync(factory);
    }

    [Fact]
    public async Task Post_PingAddressedToAnotherBot_ReturnsOkWithoutSending()
    {
        var messageSender = new RecordingTelegramMessageSender();

        using var host = new OwnDayHostFactory();
        using var factory = CreateFactory(host, messageSender);
        using var client = factory.CreateClient();

        using var request = CreateRequest(
            ValidWebhookSecret,
            "/ping@OtherBot");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(messageSender.Messages);
        await AssertNoPersistedMessagesAsync(factory);
    }

    private static async Task AssertNoPersistedMessagesAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await dbContext.TelegramOutboxMessages.ToListAsync());
        Assert.Empty(await dbContext.ProcessedTelegramUpdates.ToListAsync());
    }

    private static WebApplicationFactory<Program> CreateFactory(
        OwnDayHostFactory host,
        ITelegramMessageSender messageSender,
        ITelegramMessageCleaner? cleaner = null)
    {
        return host.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITelegramMessageSender>();

                services.AddSingleton(messageSender);
                if (cleaner is not null)
                {
                    services.RemoveAll<ITelegramMessageCleaner>();
                    services.AddSingleton(cleaner);
                }
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
                "from": { "id": {{ChatId}}, "is_bot": false, "first_name": "Test" },
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

    private sealed class RecordingTelegramMessageCleaner : ITelegramMessageCleaner
    {
        public List<(long ChatId, int MessageId)> Deletions { get; } = [];

        public Exception? Failure { get; set; }

        public Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            Deletions.Add((chatId, messageId));
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.CompletedTask;
        }
    }
}
