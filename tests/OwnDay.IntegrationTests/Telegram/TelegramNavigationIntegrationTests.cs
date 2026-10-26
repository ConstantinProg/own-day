using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Inbox;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramNavigationIntegrationTests
{
    [Theory]
    [InlineData("en", "Send a thought", "Tasks")]
    [InlineData("ru", "Отправьте мысль", "Задачи")]
    public async Task Post_EntryAndNavigation_ExposeLocalizedTextAlternatives(string locale, string introduction, string tasks)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, "/start", locale);
        await PostAsync(client, 2, "/help", locale);
        await PostAsync(client, 3, "/list", locale);
        await PostAsync(client, 4, "/settings", locale);
        await PostAsync(client, 5, "/help commands", locale);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var replies = await db.TelegramOutboxMessages.OrderBy(m => m.CreatedAt).ToArrayAsync();
        Assert.Equal(5, replies.Length);
        Assert.StartsWith(introduction, replies[0].Text, StringComparison.Ordinal);
        Assert.Contains("/stats", replies[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/ping", replies[1].Text, StringComparison.Ordinal);
        var keyboard = JsonSerializer.Deserialize<InlineKeyboardMarkup>(replies[2].ReplyMarkupJson!)!;
        Assert.Equal(tasks, keyboard.InlineKeyboard.First().First().Text);
        foreach (var button in keyboard.InlineKeyboard.SelectMany(row => row))
        {
            Assert.False(string.IsNullOrWhiteSpace(button.Text));
            Assert.Contains("/" + button.CallbackData![4..], replies[2].Text, StringComparison.Ordinal);
        }
        Assert.Contains("/language en", replies[3].Text, StringComparison.Ordinal);
        Assert.Contains("/language ru", replies[3].Text, StringComparison.Ordinal);
        Assert.All(new[] { "/capture", "/back", "/accept", "/reject", "/discard", "/process", "/add", "/wait" },
            command => Assert.Contains(command, replies[4].Text, StringComparison.Ordinal));
        Assert.Empty(await db.InboxItems.ToListAsync());
    }

    [Theory]
    [InlineData("task", "en", InboxTargetKind.Action)]
    [InlineData("project", "en", InboxTargetKind.Project)]
    [InlineData("idea", "en", InboxTargetKind.SomedayMaybe)]
    [InlineData("note", "en", InboxTargetKind.Reference)]
    [InlineData("waiting", "en", InboxTargetKind.WaitingFor)]
    [InlineData("задача", "ru", InboxTargetKind.Action)]
    [InlineData("проект", "ru", InboxTargetKind.Project)]
    [InlineData("идея", "ru", InboxTargetKind.SomedayMaybe)]
    [InlineData("заметка", "ru", InboxTargetKind.Reference)]
    [InlineData("ожидание", "ru", InboxTargetKind.WaitingFor)]
    public async Task Post_TypedProcessing_CreatesExactlyOneResultWithoutWizard(string type, string locale, InboxTargetKind kind)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, "Original", locale);
        await PostAsync(client, 2, $"/process 1 {type} Corrected", locale);
        await PostAsync(client, 2, $"/process 1 {type} Corrected", locale);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var item = await db.InboxItems.SingleAsync();
        Assert.Equal(InboxItemStatus.Processed, item.Status);
        Assert.Equal("Original", item.OriginalText);
        Assert.Equal(kind, Assert.Single(item.Targets).Kind);
        Assert.Empty(await db.TelegramInboxDrafts.ToListAsync());
        Assert.Equal(2, await db.TelegramOutboxMessages.CountAsync());
        Assert.Equal(2, await db.ProcessedTelegramUpdates.CountAsync());
        Assert.Equal(kind == InboxTargetKind.Action ? "Corrected" : null,
            await db.Actions.Select(a => a.Title).SingleOrDefaultAsync());
    }

    [Fact]
    public async Task Post_BackThroughWaitingEditor_ClearsDownstreamChoicesAndReturnsToInbox()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var commands = new[] { "/project Main", "Original", "/process 1", "5", "Edited", "Person", "1" };
        var id = 0;
        foreach (var command in commands)
        {
            await PostAsync(client, ++id, command);
        }
        foreach (var expected in new[] { InboxDraftStep.Project, InboxDraftStep.Source, InboxDraftStep.Text, InboxDraftStep.Target })
        {
            await PostAsync(client, ++id, "/back");
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            var draft = await db.TelegramInboxDrafts.SingleAsync();
            Assert.Equal(expected, draft.Step);
            Assert.Null(draft.ProjectId);
            if (expected != InboxDraftStep.Project)
            {
                Assert.Null(draft.Source);
            }
        }
        await PostAsync(client, ++id, "/back");
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await saved.TelegramInboxDrafts.ToListAsync());
        Assert.Equal(InboxItemStatus.Active, (await saved.InboxItems.SingleAsync()).Status);
        Assert.Empty(await saved.WaitingFors.ToListAsync());
        Assert.Contains(await saved.TelegramOutboxMessages.ToListAsync(), m => m.Text.StartsWith("Inbox\n", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/list")]
    [InlineData("/tasks")]
    [InlineData("/help")]
    [InlineData("/settings")]
    [InlineData("/inbox")]
    [InlineData("/result 1")]
    [InlineData("/stats")]
    [InlineData("/language")]
    public async Task Post_NavigationDuringEditor_NextThoughtBecomesNewEntry(string command)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        foreach (var (id, text) in new[] { (1, "Original"), (2, "/process 1"), (3, "1"), (4, command), (5, "New thought") })
        {
            await PostAsync(client, id, text);
        }
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(new[] { "Original", "New thought" }, await db.InboxItems.OrderBy(i => i.Id).Select(i => i.OriginalText).ToArrayAsync());
        Assert.Empty(await db.TelegramInboxDrafts.ToListAsync());
        Assert.Equal(2, await db.TelegramInboxDestinations.CountAsync());
        Assert.Equal(5, await db.TelegramOutboxMessages.CountAsync());
        Assert.Empty(await db.Actions.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_CaptureDuringTextOrSourcePrompt_SavesNewEntryAndClosesEditor(bool source)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, "Original");
        await PostAsync(client, 2, "/process 1");
        await PostAsync(client, 3, "5");
        if (source)
        {
            await PostAsync(client, 4, ".");
        }
        await PostAsync(client, 5, "/capture New thought");
        await PostAsync(client, 5, "/capture New thought");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(2, await db.InboxItems.CountAsync());
        Assert.Equal(2, await db.InboxAnalysisJobs.CountAsync());
        Assert.Equal(2, await db.TelegramInboxDestinations.CountAsync());
        Assert.Empty(await db.TelegramInboxDrafts.ToListAsync());
        Assert.Empty(await db.WaitingFors.ToListAsync());
    }

    [Fact]
    public async Task Post_OriginalProcessedWhileEditorOpen_PreservesNextThoughtAndDestination()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, "Original");
        await PostAsync(client, 2, "/process 1");
        using (var scope = factory.Services.CreateScope())
        {
            var processing = scope.ServiceProvider.GetRequiredService<InboxProcessingService>();
            await processing.ProcessAsync(new UserId(101), new(1, InboxTargetKind.Reference, "Existing result"), CancellationToken.None);
        }
        await PostAsync(client, 3, "New thought");
        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(2, await db.InboxItems.CountAsync());
        Assert.Equal(2, await db.TelegramInboxDestinations.CountAsync());
        Assert.Empty(await db.TelegramInboxDrafts.ToListAsync());
        Assert.Equal("Existing result", (await db.References.SingleAsync()).Text);
    }

    [Fact]
    public async Task Post_ResultAliasAndManualAlias_ProduceSameViews()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, "Original");
        await PostAsync(client, 2, "/result 1");
        await PostAsync(client, 3, "/inbox_result 1");
        await PostAsync(client, 4, "/process 1");
        await PostAsync(client, 5, "/inbox 1");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var replies = await db.TelegramOutboxMessages.OrderBy(m => m.CreatedAt).Select(m => m.Text).ToArrayAsync();
        Assert.Equal(replies[1], replies[2]);
        Assert.Equal(replies[3], replies[4]);
        Assert.Single(await db.TelegramInboxDrafts.ToListAsync());
    }

    [Theory]
    [InlineData("en", "Tokens:", "Background job")]
    [InlineData("ru", "Токены:", "Повторов фоновых")]
    public async Task Post_Statistics_SummaryOmitsDiagnosticsAndDetailsPreserveThem(string locale, string tokens, string workers)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, "/stats 7d", locale);
        await PostAsync(client, 2, "/stats 7d details", locale);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var replies = await db.TelegramOutboxMessages.OrderBy(m => m.CreatedAt).Select(m => m.Text).ToArrayAsync();
        Assert.DoesNotContain(tokens, replies[0], StringComparison.Ordinal);
        Assert.DoesNotContain(workers, replies[0], StringComparison.Ordinal);
        Assert.Contains("/stats 7d details", replies[0], StringComparison.Ordinal);
        Assert.Contains(tokens, replies[1], StringComparison.Ordinal);
        Assert.Contains(workers, replies[1], StringComparison.Ordinal);
        Assert.True(replies[0].Length < replies[1].Length);
    }

    [Theory]
    [InlineData("en", "Language changed to English.")]
    [InlineData("ru", "Язык изменён на русский.")]
    public async Task Post_FirstCommandSelectsLanguage_PersistsPreferenceWithoutOpeningDialog(string locale, string expected)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, "/language " + locale);
        await PostAsync(client, 1, "/language " + locale);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var preference = await db.TelegramUserLanguages.SingleAsync();
        Assert.Equal(locale, preference.Locale);
        Assert.Null(preference.SelectionExpiresAt);
        Assert.Equal(expected, (await db.TelegramOutboxMessages.SingleAsync()).Text);
        Assert.Empty(await db.InboxItems.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_ProseDuringChoiceOrConfirmation_CapturesNewEntry(bool confirmation)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, "Original");
        await PostAsync(client, 2, "/process 1");
        if (confirmation)
        {
            await PostAsync(client, 3, "2");
            await PostAsync(client, 4, ".");
        }
        await PostAsync(client, 5, "A different thought");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(2, await db.InboxItems.CountAsync());
        Assert.Empty(await db.TelegramInboxDrafts.ToListAsync());
        Assert.Empty(await db.Projects.ToListAsync());
        Assert.Equal(2, await db.TelegramInboxDestinations.CountAsync());
    }

    [Fact]
    public async Task Post_CancelLegacyOverlappingDialogs_ClearsBothAndRepliesOncePerCommand()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, "Original");
        await PostAsync(client, 2, "/process 1");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            db.TelegramUserLanguages.Add(new() { UserId = 101, SelectionExpiresAt = DateTime.UtcNow.AddMinutes(5) });
            await db.SaveChangesAsync();
        }
        await PostAsync(client, 3, "/cancel");
        await PostAsync(client, 3, "/cancel");
        await PostAsync(client, 4, "/cancel");
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Null((await saved.TelegramUserLanguages.SingleAsync()).SelectionExpiresAt);
        Assert.Empty(await saved.TelegramInboxDrafts.ToListAsync());
        Assert.Equal(4, await saved.TelegramOutboxMessages.CountAsync());
        Assert.Single(await saved.InboxItems.ToListAsync());
    }

    [Fact]
    public async Task Post_ProseInLegacyOverlappingLanguagePicker_IsCapturedInsteadOfEditingOldDraft()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, "Original");
        await PostAsync(client, 2, "/process 1");
        await PostAsync(client, 3, "1");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            db.TelegramUserLanguages.Add(new() { UserId = 101, SelectionExpiresAt = DateTime.UtcNow.AddMinutes(5) });
            await db.SaveChangesAsync();
        }
        await PostAsync(client, 4, "New thought");
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(new[] { "Original", "New thought" }, await saved.InboxItems.OrderBy(i => i.Id).Select(i => i.OriginalText).ToArrayAsync());
        Assert.Empty(await saved.TelegramInboxDrafts.ToListAsync());
        Assert.Null((await saved.TelegramUserLanguages.SingleAsync()).SelectionExpiresAt);
    }

    [Theory]
    [InlineData("nav:list")]
    [InlineData("ir:1")]
    public async Task Post_NavigationButton_ClosesEditorAndDeduplicatesReply(string data)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, "Original");
        await PostAsync(client, 2, "/process 1");
        var update = new
        {
            update_id = 3,
            callback_query = new
            {
                id = "navigate", chat_instance = "test-chat", data,
                from = new { id = 101, is_bot = false, first_name = "Test" },
                message = new
                {
                    message_id = 100, date = 1792929600,
                    chat = new { id = 101, type = "private" }, text = "Old navigation"
                }
            }
        };
        await SendAsync(client, update);
        await SendAsync(client, update);
        await PostAsync(client, 4, "New thought");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(2, await db.InboxItems.CountAsync());
        Assert.Equal(4, await db.TelegramOutboxMessages.CountAsync());
        Assert.Empty(await db.TelegramInboxDrafts.ToListAsync());
    }

    [Theory]
    [InlineData("/process 1 task")]
    [InlineData("/capture Another thought")]
    public async Task Post_NewCommandReplyFails_RollsBackMutationAndAllowsSameUpdateRetry(string command)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, "Original");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_outbox BEFORE INSERT ON telegram_outbox_messages
                BEGIN SELECT RAISE(ABORT, 'Simulated storage failure'); END;
                """);
        }
        await PostAsync(client, 2, command, expectedStatus: HttpStatusCode.InternalServerError);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            Assert.Equal(InboxItemStatus.Active, (await db.InboxItems.SingleAsync()).Status);
            Assert.Empty(await db.Actions.ToListAsync());
            Assert.Single(await db.TelegramOutboxMessages.ToListAsync());
            Assert.Single(await db.ProcessedTelegramUpdates.ToListAsync());
            Assert.Single(await db.TelegramInboxDestinations.ToListAsync());
            Assert.Single(await db.InboxAnalysisJobs.ToListAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_outbox");
        }
        await PostAsync(client, 2, command);
        await PostAsync(client, 2, command);
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(2, await saved.TelegramOutboxMessages.CountAsync());
        Assert.Equal(2, await saved.ProcessedTelegramUpdates.CountAsync());
        Assert.Equal(command.StartsWith("/process", StringComparison.Ordinal) ? 1 : 0, await saved.Actions.CountAsync());
        Assert.Equal(command.StartsWith("/capture", StringComparison.Ordinal) ? 2 : 1, await saved.InboxItems.CountAsync());
    }

    private static Task PostAsync(HttpClient client, int id, string text, string locale = "en",
        HttpStatusCode expectedStatus = HttpStatusCode.OK) => SendAsync(client, new
    {
        update_id = id,
        message = new
        {
            message_id = id, date = 1792929600,
            from = new { id = 101, is_bot = false, first_name = "Test", language_code = locale },
            chat = new { id = 101, type = "private" }, text
        }
    }, expectedStatus);

    private static async Task SendAsync(HttpClient client, object update, HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        request.Content = new StringContent(JsonSerializer.Serialize(update), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
    }
}
