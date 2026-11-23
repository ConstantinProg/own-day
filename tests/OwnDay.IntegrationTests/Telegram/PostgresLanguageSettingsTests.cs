using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Learning;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Delivery;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_LanguageCommands_PersistAcrossRestartWithoutChangingLearning()
    {
        var probe = new Probe("");
        var lesson = Lesson.Start(_user, "Algebra", "Saved theory", "Saved question?");
        var context = DialogueContext.Create(_user, lesson, _now);
        context.OpenConfirmation(Guid.NewGuid(), DialogueOperationKind.Finish, "", new string('A', 64), _now);
        await using (var seed = Open())
        {
            seed.AddRange(lesson, context);
            await seed.SaveChangesAsync();
        }

        using (var first = ChoiceHost(probe, new()).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<LearningRequestProcessingOptions>(options => options.AcceptancePaused = true))))
        using (var client = first.CreateClient())
        {
            await PostLanguageCommandAsync(client, 1, "/settings", "fr");
            Assert.StartsWith("Settings\nCurrent language: English (automatic).", (await LatestLanguageReplyAsync()).Text);
            await PostLanguageCommandAsync(client, 2, "/language ru", "en");
            Assert.Equal("Язык изменён на русский.", (await LatestLanguageReplyAsync()).Text);
            await PostLanguageCommandAsync(client, 2, "/language ru", "en");
        }

        using (var restarted = ChoiceHost(probe, new()))
        using (var client = restarted.CreateClient())
        {
            await using var db = Open();
            var stamp = await ReadLanguageRowVersionAsync(db);
            await PostLanguageCommandAsync(client, 3, "/language ru", "en");
            await PostLanguageCommandAsync(client, 4, "/language fr", "en");
            Assert.StartsWith("Текущий язык можно посмотреть", (await LatestLanguageReplyAsync()).Text);
            Assert.Equal(stamp, await ReadLanguageRowVersionAsync(db));
            await PostLanguageCommandAsync(client, 5, "/settings", "en");
            Assert.StartsWith("Настройки\nТекущий язык: русский (выбран вами).", (await LatestLanguageReplyAsync()).Text);
            await PostLanguageCommandAsync(client, 6, "/settings", "ru-RU", owner: 202);
            Assert.StartsWith("Настройки\nТекущий язык: русский (определён автоматически).", (await LatestLanguageReplyAsync(202)).Text);
            await PostLanguageCommandAsync(client, 7, "/language en", "ru");
            Assert.Equal("Language changed to English.", (await LatestLanguageReplyAsync()).Text);
            await PostLanguageCommandAsync(client, 8, "/settings", "ru");
            Assert.StartsWith("Settings\nCurrent language: English (set by you).", (await LatestLanguageReplyAsync()).Text);
        }

        await using var saved = Open();
        var preferences = await saved.TelegramUserLanguages.OrderBy(row => row.UserId).ToListAsync();
        Assert.Equal("en", preferences[0].Locale);
        Assert.Null(preferences[1].Locale);
        var current = await saved.Set<DialogueContext>().SingleAsync();
        Assert.Equal(context.Revision, current.Revision);
        Assert.Equal(context.PromptId, current.PromptId);
        Assert.Equal(context.PendingConfirmation, current.PendingConfirmation);
        var active = await saved.Set<Lesson>().SingleAsync();
        Assert.Equal(lesson.Id, active.Id);
        Assert.Equal(LessonStatus.AwaitingAnswer, active.Status);
        Assert.Equal(lesson.Question, active.Question);
        Assert.Equal(lesson.Theory, active.Theory);
        Assert.Equal(8, await saved.ProcessedTelegramUpdates.CountAsync());
        Assert.Equal(8, await saved.TelegramOutboxMessages.CountAsync());
        Assert.Empty(await saved.Set<LearningRequest>().ToListAsync());
        Assert.Empty(await saved.InboxItems.ToListAsync());
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_LanguageButtons_RejectForeignTamperedSupersededAndExpiredChoices()
    {
        var clock = new LanguageTestClock(_now);
        var acknowledgements = new ChoiceAcknowledgements();
        using var host = ChoiceHost(new Probe(""), acknowledgements).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock))));
        using var client = host.CreateClient();

        await PostLanguageCommandAsync(client, 1, "/language", "en");
        var first = await LanguageButtonsAsync();
        Assert.Equal(new[] { "Русский", "English" }, first.Select(button => button.Text).ToArray());
        var russian = first[0].CallbackData!;
        Assert.True(russian.Length <= 64);
        await PostChoiceCallbackAsync(client, 2, russian, owner: 202);
        var tampered = russian[..^1] + (russian[^1] == 'A' ? 'B' : 'A');
        await PostChoiceCallbackAsync(client, 3, tampered);

        await PostLanguageCommandAsync(client, 4, "/language", "en");
        var second = await LanguageButtonsAsync();
        await PostChoiceCallbackAsync(client, 5, russian, clientLanguage: "en");
        Assert.StartsWith("This language menu is out of date.", (await LatestLanguageReplyAsync()).Text);
        await PostChoiceCallbackAsync(client, 6, second[0].CallbackData!);
        await PostChoiceCallbackAsync(client, 6, second[0].CallbackData!);
        Assert.Equal("Язык изменён на русский.", (await LatestLanguageReplyAsync()).Text);
        await PostChoiceCallbackAsync(client, 7, second[1].CallbackData!);
        Assert.StartsWith("Это меню устарело.", (await LatestLanguageReplyAsync()).Text);

        await PostLanguageCommandAsync(client, 8, "/language", "en");
        var third = await LanguageButtonsAsync();
        await PostChoiceCallbackAsync(client, 9, russian);
        Assert.StartsWith("Это меню устарело.", (await LatestLanguageReplyAsync()).Text);
        clock.Now = _now.AddMinutes(11);
        await PostChoiceCallbackAsync(client, 10, third[1].CallbackData!);
        Assert.StartsWith("Это меню устарело.", (await LatestLanguageReplyAsync()).Text);

        await using var saved = Open();
        Assert.Equal("ru", (await saved.TelegramUserLanguages.SingleAsync()).Locale);
        Assert.Equal(1, await saved.TelegramOutboxMessages.CountAsync(reply => reply.Text == "Язык изменён на русский."));
        Assert.Equal(8, acknowledgements.Ids.Count);
        Assert.Empty(await saved.Set<LearningRequest>().ToListAsync());
    }

    [Fact]
    public async Task Post_LanguageOutboxCommitFails_RollsBackPreferenceAndDeduplication()
    {
        using (var host = Host(new Probe(""), worker: false, publicIngress: true, interceptor: new RejectReceiptCommit()))
        using (var client = host.CreateClient())
            await PostLanguageCommandAsync(client, 1, "/language ru", "en", status: HttpStatusCode.InternalServerError);

        await using var db = Open();
        Assert.Empty(await db.TelegramUserLanguages.ToListAsync());
        Assert.Empty(await db.ProcessedTelegramUpdates.ToListAsync());
        Assert.Empty(await db.TelegramOutboxMessages.ToListAsync());
        using var restarted = ChoiceHost(new Probe(""), new());
        using var retry = restarted.CreateClient();
        await PostLanguageCommandAsync(retry, 1, "/language ru", "en");
        Assert.Equal("ru", (await db.TelegramUserLanguages.SingleAsync()).Locale);
        Assert.Single(await db.ProcessedTelegramUpdates.ToListAsync());
        Assert.Single(await db.TelegramOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Dispatch_StudyInvitation_UsesSavedLanguageAndPreservesTopic()
    {
        await SeedChoiceProgramAsync();
        await using (var seed = Open())
        {
            seed.Add(StudySchedule.Create(_user, "UTC", 1, [new(TimeOnly.MinValue, new TimeOnly(23, 59))],
                _now.AddDays(-2), StudyReminderType.Study));
            await seed.SaveChangesAsync();
        }
        using var host = ChoiceHost(new Probe(""), new());
        using var client = host.CreateClient();
        await PostLanguageCommandAsync(client, 1, "/language en", "ru");
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<StudyInvitationDispatcher>().DispatchBatchAsync(default);

        var reply = await LatestLanguageReplyAsync();
        Assert.StartsWith("Time to study: Математика", reply.Text);
        Assert.Equal("Start", (await LanguageButtonsAsync())[0].Text);
    }

    [Fact]
    public async Task Process_ExplicitLanguageAtIntake_LocalizesQueuedLearningAndProviderInput()
    {
        var probe = new Probe("") { Material = new("Original theory", "Original question?") };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await PostLanguageCommandAsync(client, 1, "/language ru", "en");
        await PostLanguageCommandAsync(client, 2, "/learn Algebra", "en");
        await PostLanguageCommandAsync(client, 3, "/language en", "en");
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var db = Open();
        var request = await db.Set<LearningRequest>().SingleAsync();
        var step = await db.Set<LearningProviderStep>().SingleAsync();
        using var input = JsonDocument.Parse(step.Input);
        Assert.Equal("ru", input.RootElement.GetProperty("preferredLocale").GetString());
        Assert.Contains(await db.TelegramOutboxMessages.Where(reply => reply.LearningRequestId == request.Id).ToListAsync(),
            reply => reply.Text.StartsWith("Original question?", StringComparison.Ordinal) && reply.Text.Contains("Напишите ответ", StringComparison.Ordinal));
        Assert.Equal("Original theory", (await db.Set<Lesson>().SingleAsync()).Theory);
    }

    private static Task<long> ReadLanguageRowVersionAsync(OwnDayDbContext db) =>
        db.Database.SqlQuery<long>($"""SELECT xmin::text::bigint AS "Value" FROM telegram_user_languages WHERE user_id = {101}""").SingleAsync();

    private async Task<TelegramOutboxMessage> LatestLanguageReplyAsync(long owner = 101)
    {
        await using var db = Open();
        return await db.TelegramOutboxMessages.Where(reply => reply.ChatId == owner)
            .OrderByDescending(reply => reply.DeliveryOrder).FirstAsync();
    }

    private async Task<InlineKeyboardButton[]> LanguageButtonsAsync()
    {
        var reply = await LatestLanguageReplyAsync();
        return JsonSerializer.Deserialize<InlineKeyboardMarkup>(reply.ReplyMarkupJson!)!.InlineKeyboard.SelectMany(row => row).ToArray();
    }

    private static async Task PostLanguageCommandAsync(HttpClient client, int id, string text, string clientLanguage,
        long owner = 101, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        request.Content = JsonContent.Create(new
        {
            update_id = id,
            message = new
            {
                message_id = id, date = 1790244000, chat = new { id = owner, type = "private" },
                from = new { id = owner, is_bot = false, first_name = "Learner", language_code = clientLanguage }, text
            }
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
    }

    private sealed class LanguageTestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
