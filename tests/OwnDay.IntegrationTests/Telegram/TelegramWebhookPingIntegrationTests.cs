using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Dialogue;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Commands;
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
    public async Task Post_PingWithValidSecret_QueuesDialogueWithoutDirectDelivery()
    {
        var messageSender = new RecordingTelegramMessageSender();
        var cleaner = new RecordingTelegramMessageCleaner();

        using var host = new OwnDayHostFactory();
        using var factory = CreateFactory(host, messageSender, cleaner);
        using var client = factory.CreateClient();

        using var request = CreatePingRequest(
            ValidWebhookSecret);

        using var response = await client.SendAsync(request);

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        Assert.Empty(messageSender.Messages);
        Assert.Equal((ChatId, 42), Assert.Single(cleaner.Deletions));

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var replies = await dbContext.TelegramOutboxMessages.OrderBy(reply => reply.CreatedAt).ToListAsync();
        Assert.Equal(2, replies.Count);
        Assert.Equal(TelegramDialoguePresenter.Introduction, replies[0].Text);
        Assert.All(replies, reply =>
        {
            Assert.Equal(ChatId, reply.ChatId);
            Assert.Equal(OutboxMessageStatus.Pending, reply.Status);
        });
        Assert.Single(await dbContext.Set<DialogueContext>().ToListAsync());
        var processed = Assert.Single(await dbContext.ProcessedTelegramUpdates.ToListAsync());
        Assert.Equal(100001, processed.UpdateId);
        Assert.NotNull(processed.ProcessedAt);
    }

    [Fact]
    public async Task Post_PrivateText_QueuesDialogueRequestAndPreservesSourceMessage()
    {
        var cleaner = new RecordingTelegramMessageCleaner();
        using var host = new OwnDayHostFactory();
        using var factory = CreateFactory(host, new RecordingTelegramMessageSender(), cleaner);
        using var client = factory.CreateClient();
        using var request = CreateRequest(ValidWebhookSecret, "buy milk");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(cleaner.Deletions);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.InboxItems.ToListAsync());
        Assert.Empty(await db.TelegramOutboxMessages.ToListAsync());
        var receipt = Assert.Single(await db.Set<LearningRequest>().ToListAsync());
        Assert.Equal("learning_text", receipt.PayloadKind);
        Assert.Equal(LearningRequestStatus.Ready, receipt.Status);
    }

    [Fact]
    public async Task Post_CommandDeletionFails_ReturnsOkWithCommittedDialogueAndReplies()
    {
        var cleaner = new RecordingTelegramMessageCleaner
        {
            Failure = new InvalidOperationException("Telegram rejected deletion")
        };
        using var host = new OwnDayHostFactory();
        using var factory = CreateFactory(host, new RecordingTelegramMessageSender(), cleaner);
        using var client = factory.CreateClient();
        using var request = CreateRequest(ValidWebhookSecret, "/add Buy milk");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((ChatId, 42), Assert.Single(cleaner.Deletions));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.Actions.ToListAsync());
        Assert.Single(await db.Set<DialogueContext>().ToListAsync());
        Assert.NotNull(Assert.Single(await db.ProcessedTelegramUpdates.ToListAsync()).ProcessedAt);
        Assert.Equal(2, await db.TelegramOutboxMessages.CountAsync());
        Assert.All(await db.TelegramOutboxMessages.ToListAsync(), reply => Assert.Equal(OutboxMessageStatus.Pending, reply.Status));
    }

    [Fact]
    public async Task Post_LegacyLanguageCommand_DoesNotConsumeTextAsLanguageSelection()
    {
        using var host = new OwnDayHostFactory();
        using var factory = CreateFactory(host, new RecordingTelegramMessageSender());
        using var client = factory.CreateClient();
        foreach (var (id, command) in new[]
        {
            (100001, "/language"),
            (100002, "1"),
            (100003, "/help")
        })
        {
            using var request = CreateRequest(ValidWebhookSecret, command, id, "ru");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.TelegramUserLanguages.ToListAsync());
        var replies = await db.TelegramOutboxMessages.ToListAsync();
        Assert.Equal(3, replies.Count);
        Assert.Single(replies, reply => reply.Text == TelegramDialoguePresenter.Introduction);
        var receipt = Assert.Single(await db.Set<LearningRequest>().ToListAsync());
        Assert.Equal("learning_text", receipt.PayloadKind);
        Assert.Equal(LearningRequestStatus.Ready, receipt.Status);
        Assert.Empty(await db.InboxItems.ToListAsync());
    }

    [Fact]
    public async Task Post_PingWithInvalidSecret_ReturnsUnauthorizedAndDoesNotSend()
    {
        var messageSender = new RecordingTelegramMessageSender();
        var cleaner = new RecordingTelegramMessageCleaner();

        using var host = new OwnDayHostFactory();
        using var factory = CreateFactory(host, messageSender, cleaner);
        using var client = factory.CreateClient();

        using var request = CreatePingRequest(
            "invalid-webhook-secret");

        using var response = await client.SendAsync(request);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            response.StatusCode);

        Assert.Empty(messageSender.Messages);
        Assert.Empty(cleaner.Deletions);
        await AssertNoPersistedMessagesAsync(factory);
    }

    [Fact]
    public async Task Post_PingAddressedToAnotherBot_ReturnsOkWithoutSending()
    {
        var messageSender = new RecordingTelegramMessageSender();
        var cleaner = new RecordingTelegramMessageCleaner();

        using var host = new OwnDayHostFactory();
        using var factory = CreateFactory(host, messageSender, cleaner);
        using var client = factory.CreateClient();

        using var request = CreateRequest(
            ValidWebhookSecret,
            "/ping@OtherBot");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(messageSender.Messages);
        Assert.Empty(cleaner.Deletions);
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
        string command,
        int updateId = 100001,
        string languageCode = "en")
    {
        var json = $$"""
            {
              "update_id": {{updateId}},
              "message": {
                "message_id": 42,
                "date": 1788728400,
                "from": { "id": {{ChatId}}, "is_bot": false, "first_name": "Test", "language_code": "{{languageCode}}" },
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

        public Task SendMessageAsync(
            long chatId,
            string text,
            string? replyMarkupJson,
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
