using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Infrastructure.Learning;
using OwnDay.Application.Inbox;
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
    [InlineData("/help commands")]
    [InlineData("/inbox")]
    [InlineData("/capture old note")]
    [InlineData("/process 1 task")]
    [InlineData("/add old task")]
    [InlineData("/list")]
    [InlineData("/settings")]
    public async Task Post_LegacyCommand_ExplainsDialogueWithoutLegacyNavigationOrMutation(string text)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, text);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var replies = await db.TelegramOutboxMessages.ToListAsync();
        Assert.NotEmpty(replies);
        Assert.All(replies, reply =>
        {
            Assert.DoesNotContain("Inbox", reply.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/capture", reply.Text, StringComparison.Ordinal);
            Assert.Null(reply.ReplyMarkupJson);
        });
        Assert.Empty(await db.InboxItems.ToListAsync());
        Assert.Empty(await db.Actions.ToListAsync());
        Assert.Empty(await db.TelegramInboxDrafts.ToListAsync());
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
