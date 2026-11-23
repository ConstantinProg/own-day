using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OwnDay.Application.Inbox;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Cleanup;
using OwnDay.Infrastructure.Telegram.Commands;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class PostgresTelegramRestartIntegrationTests
{
    [Theory]
    [InlineData("/start")]
    [InlineData("/inbox 1")]
    [InlineData("/done 1")]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Post_LegacyStateAcrossRestart_RetainsRecordsAndAcceptsOneDialogueRequest(string command)
    {
        var connection = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required.");
        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new OwnDayDbContext(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connection).Options);
#pragma warning disable EF1003 // Isolated GUID schema.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003
        try
        {
            var scoped = connection + ";Search Path=" + schema;
            var options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(scoped).Options;
            await using (var seed = new OwnDayDbContext(options))
            {
                await seed.Database.MigrateAsync();
                seed.Add(OwnDay.Domain.Actions.Action.Create(new UserId(101), "Existing task", DateTime.UtcNow));
                var item = InboxItem.Capture(new UserId(101), "Existing note", DateTime.UtcNow);
                seed.Add(item);
                await seed.SaveChangesAsync();
                seed.TelegramInboxDrafts.Add(new InboxDraft { UserId = 101, InboxItemId = item.Id, ExpiresAt = DateTime.UtcNow.AddHours(1) });
                seed.TelegramUserLanguages.Add(new TelegramUserLanguage { UserId = 101, Locale = "ru", SelectionExpiresAt = DateTime.UtcNow.AddHours(1) });
                await seed.SaveChangesAsync();
            }
            Guid prompt;
            using (var first = new PostgresHostFactory(scoped))
            using (var client = first.CreateClient())
            {
                await PostAsync(client, 1, command);
                using var scope = first.Services.CreateScope();
                prompt = (await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().Set<DialogueContext>().SingleAsync()).PromptId;
            }
            using (var restarted = new PostgresHostFactory(scoped))
            using (var client = restarted.CreateClient())
            {
                await PostAsync(client, 1, command);
                await PostAsync(client, 2, "Continue");
                await PostAsync(client, 2, "Continue");
                await PostAsync(client, 3, "/help");
            }
            await using var saved = new OwnDayDbContext(options);
            Assert.Equal(prompt, (await saved.Set<DialogueContext>().SingleAsync()).PromptId);
            Assert.Equal("Existing task", (await saved.Actions.SingleAsync()).Title);
            Assert.Equal(OwnDay.Domain.Actions.ActionStatus.Active, (await saved.Actions.SingleAsync()).Status);
            Assert.Equal("Existing note", (await saved.InboxItems.SingleAsync()).OriginalText);
            Assert.Equal(InboxItemStatus.Active, (await saved.InboxItems.SingleAsync()).Status);
            Assert.Empty(await saved.TelegramInboxDrafts.ToListAsync());
            var language = await saved.TelegramUserLanguages.SingleAsync();
            Assert.Equal("ru", language.Locale);
            Assert.Null(language.SelectionExpiresAt);
            var request = await saved.Set<LearningRequest>().SingleAsync();
            Assert.Equal(LearningRequestStatus.Ready, request.Status);
            Assert.Equal("learning_text", request.PayloadKind);
            Assert.Null(request.CompletedAt);
            Assert.Equal(3, await saved.ProcessedTelegramUpdates.CountAsync());
            var replies = await saved.TelegramOutboxMessages.ToListAsync();
            Assert.Equal(3, replies.Count);
            Assert.Single(replies, reply => reply.Text == TelegramDialoguePresenter.Introduction);
        }
        finally
        {
#pragma warning disable EF1003 // Same isolated GUID schema.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    private static async Task PostAsync(HttpClient client, long updateId, string command)
    {
        var json = $$"""
            {
              "update_id": {{updateId}},
              "message": {
                "message_id": {{updateId}},
                "date": 1790244000,
                "from": { "id": 101, "is_bot": false, "first_name": "Test" },
                "chat": { "id": 101, "type": "private" },
                "text": "{{command}}"
              }
            }
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.TryAddWithoutValidation(
            "X-Telegram-Bot-Api-Secret-Token",
            "integration-test-webhook-secret");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class PostgresHostFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(GetHostContentRoot());
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.UseSetting("Telegram:BotToken", "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi");
            builder.UseSetting("Telegram:CallbackSigningKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("Telegram:BotUsername", "OwnDayBot");
            builder.UseSetting("Telegram:WebhookSecret", "integration-test-webhook-secret");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ITelegramMessageCleaner>();
                services.AddSingleton<ITelegramMessageCleaner, NoOpTelegramMessageCleaner>();
            });
        }

        private static string GetHostContentRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OwnDay.slnx")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
            {
                throw new InvalidOperationException("Could not locate the OwnDay solution root.");
            }

            return Path.Combine(directory.FullName, "src", "OwnDay.Host");
        }

        private sealed class NoOpTelegramMessageCleaner : ITelegramMessageCleaner
        {
            public Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }
    }
}
