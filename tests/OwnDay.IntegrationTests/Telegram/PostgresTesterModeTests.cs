using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Application.Learning.Requests;
using OwnDay.Application.TesterMode;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Inbox;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Cleanup;
using OwnDay.Infrastructure.Telegram.Delivery;
using Xunit;
using TaskItem = OwnDay.Domain.Actions.Action;

namespace OwnDay.IntegrationTests.Telegram;

[Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
public sealed partial class PostgresTesterModeTests : IAsyncLifetime
{
    private static readonly UserId Tester = new(101);
    private static readonly UserId Other = new(202);
    private readonly string _schema = "ownday_test_" + Guid.NewGuid().ToString("N");
    private string _connection = string.Empty;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required.");
        _connection = connection + ";Search Path=" + _schema;
        await using var db = Open();
#pragma warning disable EF1003 // Isolated schema name contains only a fixed prefix and a GUID.
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + _schema);
#pragma warning restore EF1003
        Assert.False(db.Database.HasPendingModelChanges());
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_connection.Length == 0) return;
        await using var db = Open();
#pragma warning disable EF1003 // Same generated schema as InitializeAsync.
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA " + _schema + " CASCADE");
#pragma warning restore EF1003
    }

    [Fact]
    public async Task Post_ResetAfterHostRestart_ClearsOnlyOwnerStateAndRepeatsOnboarding()
    {
        using (var first = Host())
        using (var client = first.CreateClient())
        {
            await AuthorizeAsync(client);
            await PostAsync(client, 3, "/start");
            await PostAsync(client, 4, "Start a queued lesson");
        }
        await using (var seed = Open())
        {
            SeedLearning(seed, Tester, "Private first goal");
            SeedLearning(seed, Other, "Other user's goal");
            seed.AddRange(TaskItem.Create(Tester, "Task A", DateTime.UtcNow),
                TaskItem.Create(Other, "Task B", DateTime.UtcNow),
                TaskItem.Create(Other, "Task C", DateTime.UtcNow),
                InboxItem.Capture(Tester, "Legacy note", DateTime.UtcNow),
                new InboxOperationEvent { UserId = Tester.Value, Code = "diagnostic", At = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        using var restarted = Host();
        using var resumed = restarted.CreateClient();
        await PostAsync(resumed, 5, "/tester_status");
        await using (var before = Open())
        {
            var status = await before.TelegramOutboxMessages.OrderByDescending(message => message.DeliveryOrder).FirstAsync();
            Assert.Contains("Tasks — 1 open, 0 completed.", status.Text);
            Assert.Contains("Programs — 1. Selected program — 1 of 1", status.Text);
            Assert.Contains("Learning requests queued or processing — 1.", status.Text);
            Assert.DoesNotContain("Private first goal", status.Text);
            Assert.DoesNotContain("Other user's goal", status.Text);
        }

        await PostAsync(resumed, 7, "/tester_reset");
        var buttons = await ReadButtonsAsync();
        await PostAsync(resumed, 6, "/cancel");
        await CallbackAsync(resumed, 8, buttons[0], user: Other);
        await using (var before = Open())
            Assert.Equal(3, await before.Actions.CountAsync());
        await CallbackAsync(resumed, 9, buttons[0]);
        await CallbackAsync(resumed, 9, buttons[0]);
        await CallbackAsync(resumed, 10, buttons[0]);
        await PostAsync(resumed, 4, "Start a queued lesson");
        await PostAsync(resumed, 11, "/start");

        await using var saved = Open();
        Assert.True((await saved.Set<TesterModeState>().SingleAsync()).Authorized);
        Assert.Equal(Other, (await saved.Set<LearningProgram>().SingleAsync()).UserId);
        Assert.Equal(Other, (await saved.Set<Lesson>().SingleAsync()).UserId);
        Assert.Equal(Other, (await saved.Set<LearningEvidence>().SingleAsync()).UserId);
        Assert.Equal(2, await saved.Actions.CountAsync(action => action.UserId == Other));
        Assert.Empty(await saved.InboxItems.ToListAsync());
        Assert.Empty(await saved.Set<LearningRequest>().ToListAsync());
        Assert.Single(await saved.InboxOperationEvents.Where(item => item.UserId == Tester.Value).ToListAsync());
        var dialogue = await saved.Set<DialogueContext>().SingleAsync();
        Assert.Null(dialogue.Consultation);
        Assert.Equal(Tester, dialogue.UserId);
        var replies = await saved.TelegramOutboxMessages.Where(message => message.ChatId == Tester.Value)
            .Select(message => message.Text).ToListAsync();
        Assert.Single(replies, text => text.StartsWith("Your state was reset.", StringComparison.Ordinal));
        Assert.Single(replies, text => text == OwnDay.Infrastructure.Telegram.Commands.TelegramDialoguePresenter.EnglishIntroduction);
    }

    private OwnDayDbContext Open() => new(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(_connection).Options);

    private TesterHost Host() => new(_connection);

    private static async Task AuthorizeAsync(HttpClient client)
    {
        await PostAsync(client, 1, "/tester");
        await PostAsync(client, 2, TesterModeAuthorizationTests.Secret);
    }

    private static Task PostAsync(HttpClient client, int updateId, string text) =>
        TesterModeAuthorizationTests.PostAsync(client, updateId, text, Tester.Value);

    private async Task<string[]> ReadButtonsAsync()
    {
        await using var db = Open();
        var markup = await db.TelegramOutboxMessages.Where(message => message.ChatId == Tester.Value && message.ReplyMarkupJson != null)
            .OrderByDescending(message => message.DeliveryOrder).Select(message => message.ReplyMarkupJson).FirstAsync();
        using var json = JsonDocument.Parse(markup!);
        return json.RootElement.GetProperty("inline_keyboard").EnumerateArray()
            .SelectMany(row => row.EnumerateArray()).Select(button => button.GetProperty("callback_data").GetString()!).ToArray();
    }

    private static async Task CallbackAsync(HttpClient client, int updateId, string callbackData, UserId? user = null,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var sender = user ?? Tester;
        using var content = JsonContent.Create(new
        {
            update_id = updateId,
            callback_query = new
            {
                id = $"callback-{updateId}",
                from = new { id = sender.Value, is_bot = false, first_name = "Tester", language_code = "en" },
                chat_instance = "test-chat",
                message = new { message_id = 500, date = 1790244000, chat = new { id = sender.Value, type = "private" } },
                data = callbackData
            }
        });
        await TesterModeAuthorizationTests.PostUpdateAsync(client, content, status);
    }

    private static void SeedLearning(OwnDayDbContext db, UserId user, string goal)
    {
        var program = LearningProgram.Create(user, goal, "No experience", null, "Mathematics", [new("sum", "Sum", [])], null);
        var skill = Skill.Create(user, "Addition", "Explain addition");
        var lesson = Lesson.Start(user, "Topic", "Theory", "Question");
        lesson.LinkToProgram(program, "sum");
        lesson.LinkToSkill(skill);
        lesson.Complete("Answer", "Feedback", true);
        db.AddRange(program, LearningContext.Create(program), skill, lesson,
            LearningEvidence.FromAnswer(lesson, DateTimeOffset.UtcNow, "test"));
    }

    private sealed class TesterHost(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Default", connection);
            builder.UseSetting("Telegram:BotToken", "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi");
            builder.UseSetting("Telegram:BotUsername", "OwnDayBot");
            builder.UseSetting("Telegram:WebhookSecret", "integration-test-webhook-secret");
            builder.UseSetting("Telegram:CallbackSigningKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("TesterMode:Enabled", "true");
            builder.UseSetting("TesterMode:SecretHash", TesterModeAuthorizationTests.SecretHash);
            builder.UseSetting("InboxAnalysis:CostControl:MonthlyUserCallLimit", "1");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ITelegramMessageCleaner>();
                services.RemoveAll<ITelegramCallbackResponder>();
                services.AddSingleton<ITelegramMessageCleaner, OfflineCleaner>();
                services.AddSingleton<ITelegramCallbackResponder, OfflineCallbackResponder>();
            });
        }
    }

    private sealed class OfflineCleaner : ITelegramMessageCleaner
    {
        public Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class OfflineCallbackResponder : ITelegramCallbackResponder
    {
        public Task AnswerAsync(string callbackId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
