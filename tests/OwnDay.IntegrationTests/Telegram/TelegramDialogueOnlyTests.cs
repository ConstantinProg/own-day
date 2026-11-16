using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Infrastructure.Learning;
using OwnDay.Application.Inbox;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Application.Learning.Requests;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramDialogueOnlyTests
{
    [Theory]
    [InlineData("nav:inbox")]
    [InlineData("ir:1")]
    [InlineData("iu:1")]
    [InlineData("ia:y:11111111111111111111111111111111")]
    public async Task Post_OldInboxButton_ShowsDialogueHelpWithoutInboxEffects(string data)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        request.Content = JsonContent.Create(new
        {
            update_id = 1,
            callback_query = new
            {
                id = "old-button", chat_instance = "test", data,
                from = new { id = 101, is_bot = false, first_name = "Test" },
                message = new { message_id = 10, date = 1792929600,
                    chat = new { id = 101, type = "private" }, text = "Old menu" }
            }
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var replies = await db.TelegramOutboxMessages.ToListAsync();
        Assert.NotEmpty(replies);
        Assert.All(replies, reply => Assert.Null(reply.ReplyMarkupJson));
        Assert.DoesNotContain(replies, reply => reply.Text.Contains("Inbox", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await db.InboxItems.ToListAsync());
        Assert.Empty(await db.Actions.ToListAsync());
        Assert.Empty(await db.Set<LearningRequest>().ToListAsync());
    }

    [Fact]
    public async Task Post_AcceptancePaused_DoesNotFallBackToInboxOrCommitReceipt()
    {
        using var factory = new OwnDayHostFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.Configure<LearningRequestProcessingOptions>(
                options => options.AcceptancePaused = true)));
        using var client = factory.CreateClient();
        await PostAsync(client, 1, "Continue", HttpStatusCode.InternalServerError);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.InboxItems.ToListAsync());
        Assert.Empty(await db.ProcessedTelegramUpdates.ToListAsync());
        Assert.Empty(await db.Set<LearningRequest>().ToListAsync());
        Assert.Empty(await db.TelegramOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Post_RequestSaveFails_RollsBackReceiptAndCanBeRedelivered()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_learning BEFORE INSERT ON learning_requests
                BEGIN SELECT RAISE(ABORT, 'test receipt failure'); END;
                """);
        }
        await PostAsync(client, 1, "Continue", HttpStatusCode.InternalServerError);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            Assert.Empty(await db.ProcessedTelegramUpdates.ToListAsync());
            Assert.Empty(await db.InboxItems.ToListAsync());
            Assert.Empty(await db.Set<LearningRequest>().ToListAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_learning");
        }
        await PostAsync(client, 1, "Continue");
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Single(await saved.Set<LearningRequest>().ToListAsync());
        Assert.Single(await saved.ProcessedTelegramUpdates.ToListAsync());
    }

    [Fact]
    public async Task Post_OrdinaryTextWithoutEnrollment_CommitsOneDialogueRequestAndNoInboxItem()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, "I want to learn English");
        await PostAsync(client, 1, "I want to learn English");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var request = await db.Set<LearningRequest>().SingleAsync();
        Assert.Equal("learning_text", request.PayloadKind);
        Assert.Equal(LearningRequestStatus.Ready, request.Status);
        Assert.Contains("\"consultationAllowed\":true", request.NormalizedInput, StringComparison.Ordinal);
        Assert.Empty(await db.InboxItems.ToListAsync());
        Assert.Single(await db.ProcessedTelegramUpdates.ToListAsync());
    }

    [Fact]
    public async Task Post_LegacyEditorsExist_TextStillEntersDialogue()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            db.TelegramInboxDrafts.Add(new InboxDraft { UserId = 101, InboxItemId = 999,
                ExpiresAt = DateTime.UtcNow.AddHours(1) });
            db.TelegramUserLanguages.Add(new TelegramUserLanguage { UserId = 101,
                SelectionExpiresAt = DateTime.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();
        }
        await PostAsync(client, 1, "Continue");
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal("learning_text", (await saved.Set<LearningRequest>().SingleAsync()).PayloadKind);
        Assert.Empty(await saved.TelegramInboxDrafts.ToListAsync());
        Assert.Null((await saved.TelegramUserLanguages.SingleAsync()).SelectionExpiresAt);
        Assert.Empty(await saved.InboxItems.ToListAsync());
    }

    [Theory]
    [InlineData("/start")]
    [InlineData("/ping")]
    [InlineData("/unknown")]
    [InlineData("/help")]
    [InlineData("/help commands")]
    [InlineData("/language")]
    [InlineData("/language ru")]
    [InlineData("/inbox")]
    [InlineData("/inbox 1")]
    [InlineData("/capture old note")]
    [InlineData("/add old task")]
    [InlineData("/done 1")]
    [InlineData("/tasks")]
    [InlineData("/list")]
    [InlineData("/project old project")]
    [InlineData("/projects")]
    [InlineData("/idea old idea")]
    [InlineData("/ideas")]
    [InlineData("/note old note")]
    [InlineData("/notes")]
    [InlineData("/wait old item")]
    [InlineData("/waiting")]
    [InlineData("/settings")]
    [InlineData("/stats")]
    [InlineData("/process 1 task")]
    [InlineData("/discard 1")]
    [InlineData("/back")]
    [InlineData("/result 1")]
    [InlineData("/inbox_result 1")]
    [InlineData("/undo 1")]
    [InlineData("/accept 1 1")]
    [InlineData("/reject 1 1")]
    [InlineData("/inbox_accept 11111111111111111111111111111111")]
    [InlineData("/inbox_reject 11111111111111111111111111111111")]
    public async Task Post_LegacyCommand_RetainsExistingRecordsAndQueuesDialogueOnce(string text)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        long actionId;
        long inboxId;
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            var action = OwnDay.Domain.Actions.Action.Create(new UserId(101), "Existing task", DateTime.UtcNow);
            var inbox = InboxItem.Capture(new UserId(101), "Existing note", DateTime.UtcNow);
            db.AddRange(action, inbox);
            await db.SaveChangesAsync();
            actionId = action.Id;
            inboxId = inbox.Id;
            db.TelegramInboxDrafts.Add(new InboxDraft { UserId = 101, InboxItemId = inbox.Id,
                ExpiresAt = DateTime.UtcNow.AddHours(1) });
            db.TelegramUserLanguages.Add(new TelegramUserLanguage { UserId = 101, Locale = "en",
                SelectionExpiresAt = DateTime.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();
        }

        await PostAsync(client, 1, text);
        await PostAsync(client, 1, text);

        using var scope = factory.Services.CreateScope();
        var saved = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var replies = await saved.TelegramOutboxMessages.OrderBy(reply => reply.CreatedAt).ToListAsync();
        Assert.Equal(2, replies.Count);
        Assert.Equal(TelegramDialoguePresenter.Introduction, replies[0].Text);
        Assert.All(replies, reply =>
        {
            Assert.DoesNotContain("/capture", reply.Text, StringComparison.Ordinal);
            Assert.Null(reply.ReplyMarkupJson);
            Assert.Equal(101, reply.ChatId);
        });
        var task = Assert.Single(await saved.Actions.ToListAsync());
        Assert.Equal(actionId, task.Id);
        Assert.Equal("Existing task", task.Title);
        Assert.Equal(OwnDay.Domain.Actions.ActionStatus.Active, task.Status);
        var item = Assert.Single(await saved.InboxItems.ToListAsync());
        Assert.Equal(inboxId, item.Id);
        Assert.Equal("Existing note", item.OriginalText);
        Assert.Empty(await saved.TelegramInboxDrafts.ToListAsync());
        var language = await saved.TelegramUserLanguages.SingleAsync();
        Assert.Equal("en", language.Locale);
        Assert.Null(language.SelectionExpiresAt);
        Assert.Single(await saved.ProcessedTelegramUpdates.ToListAsync());
        Assert.Empty(await saved.Set<LearningRequest>().ToListAsync());
    }

    private static async Task PostAsync(HttpClient client, int id, string text,
        HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        request.Content = JsonContent.Create(new
        {
            update_id = id,
            message = new
            {
                message_id = id, date = 1792929600,
                from = new { id = 101, is_bot = false, first_name = "Test" },
                chat = new { id = 101, type = "private" }, text
            }
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
    }
}
