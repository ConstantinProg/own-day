using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Telegram.Delivery;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_MultipleChoiceConcurrentCallbacksAndRestart_PersistsDraftAndCommitsOneAssessment()
    {
        var probe = new Probe("") { Material = ChoiceMaterial(ChoiceFormat.MultipleChoice) };
        var acknowledgements = new ChoiceAcknowledgements();
        InlineKeyboardButton[] buttons;
        int firstOption;
        using (var host = ChoiceHost(probe, acknowledgements))
        using (var client = host.CreateClient())
        {
            await SendChoiceTextAsync(client, 1, "/learn Сложение");
            await DrainChoiceRequestsAsync(host.Services, 1);
            var initial = await ChoiceKeyboardAsync();
            buttons = initial.Buttons;
            Assert.Equal(3, buttons.Length);
            Assert.All(buttons, button => Assert.Contains("не выбрано", button.Text));
            Assert.Contains("Выбрано — ничего", initial.Text);
            Assert.DoesNotContain("Обоснование", initial.Text);
            Assert.All(buttons, button => Assert.Matches("^lc:[a-f0-9]{32}:1:1:t:[1-6]$", button.CallbackData!));

            var emptySubmit = buttons[0].CallbackData!.Replace(":t:1", ":s:0", StringComparison.Ordinal);
            await PostChoiceCallbackAsync(client, 2, emptySubmit);
            await DrainChoiceRequestsAsync(host.Services, 1);
            Assert.Contains("Выбрано — ничего", (await ChoiceKeyboardAsync()).Text);

            await PostChoiceCallbackAsync(client, 3, buttons[0].CallbackData!);
            await PostChoiceCallbackAsync(client, 3, buttons[0].CallbackData!);
            await DrainChoiceRequestsAsync(host.Services, 1);

            var afterToggle = await ChoiceKeyboardAsync();
            Assert.Contains("Выбрано — А", afterToggle.Text);
            Assert.Equal("А. Первый вариант — выбрано", afterToggle.Buttons[0].Text);
            Assert.Equal("Ответить", afterToggle.Buttons[^1].Text);

            await Task.WhenAll(PostChoiceCallbackAsync(client, 4, afterToggle.Buttons[1].CallbackData!),
                PostChoiceCallbackAsync(client, 5, afterToggle.Buttons[2].CallbackData!));
            await DrainChoiceRequestsAsync(host.Services, 2);
            await using var observer = Open();
            var draft = (await observer.Set<Lesson>().SingleAsync()).Choice!.Draft;
            Assert.Equal(2, draft.Length);
            Assert.Contains(1, draft);
            Assert.Empty(await observer.Set<LearningEvidence>().ToListAsync());
            firstOption = draft.Single(id => id != 1);
            Assert.Contains(await observer.TelegramOutboxMessages.ToListAsync(), reply => reply.Text.Contains("кнопка устарела", StringComparison.Ordinal));
            await PostChoiceCallbackAsync(client, 6, buttons[0].CallbackData!);
        }

        using (var restarted = ChoiceHost(probe, acknowledgements))
        using (var client = restarted.CreateClient())
        {
            await DrainChoiceRequestsAsync(restarted.Services, 1);
            await SendChoiceTextAsync(client, 7, "Продолжим");
            await DrainChoiceRequestsAsync(restarted.Services, 1);
            var restored = await ChoiceKeyboardAsync();
            Assert.Contains(firstOption == 2 ? "Выбрано — А, Б" : "Выбрано — А, В", restored.Text);
            Assert.Equal(4, restored.Buttons.Length);
            if (firstOption == 3)
            {
                await PostChoiceCallbackAsync(client, 8, restored.Buttons[2].CallbackData!);
                await DrainChoiceRequestsAsync(restarted.Services, 1);
                restored = await ChoiceKeyboardAsync();
                await PostChoiceCallbackAsync(client, 9, restored.Buttons[1].CallbackData!);
                await DrainChoiceRequestsAsync(restarted.Services, 1);
                restored = await ChoiceKeyboardAsync();
            }

            await Task.WhenAll(PostChoiceCallbackAsync(client, 10, restored.Buttons[^1].CallbackData!),
                PostChoiceCallbackAsync(client, 11, restored.Buttons[^1].CallbackData!));
            await DrainChoiceRequestsAsync(restarted.Services, 2);
            await PostChoiceCallbackAsync(client, 12, buttons[0].CallbackData!);
            await DrainChoiceRequestsAsync(restarted.Services, 1);
        }

        await using var saved = Open();
        var lesson = await saved.Set<Lesson>().SingleAsync();
        var attempt = Assert.Single(lesson.Choice!.Attempts);
        Assert.Equal(new[] { 1, 2 }, attempt.Selected.ToArray());
        Assert.Equal(ChoiceOutcome.Correct, attempt.Outcome);
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        var evidence = Assert.Single(await saved.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(lesson.Id, evidence.LessonId);
        Assert.Equal(ChoiceOutcome.Correct, evidence.ChoiceEvidence!.FinalOutcome);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(2, acknowledgements.Ids.Count(id => id == "choice-3"));
        Assert.Equal(await saved.Set<LearningRequest>().CountAsync(), await saved.Set<LearningAppliedOperation>().CountAsync());
    }

    [Theory]
    [InlineData(ChoiceFormat.SingleChoice)]
    [InlineData(ChoiceFormat.TrueFalse)]
    public async Task Post_ImmediateChoice_ForeignAndDuplicateCallbacksDoNotCreateAttempts(ChoiceFormat format)
    {
        var probe = new Probe("") { Material = ChoiceMaterial(format) };
        var acknowledgements = new ChoiceAcknowledgements();
        using var host = ChoiceHost(probe, acknowledgements);
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn Сложение");
        await DrainChoiceRequestsAsync(host.Services, 1);
        var initial = await ChoiceKeyboardAsync();
        var right = initial.Buttons[0].CallbackData!;
        Assert.Equal(format == ChoiceFormat.TrueFalse ? "А. Верно" : "А. Первый вариант", initial.Buttons[0].Text);
        Assert.All(initial.Buttons, button =>
        {
            Assert.InRange(Encoding.UTF8.GetByteCount(button.CallbackData!), 1, 64);
            Assert.Matches("^lc:[a-f0-9]{32}:1:1:a:[1-6]$", button.CallbackData!);
        });

        await PostChoiceCallbackAsync(client, 2, right, owner: 202);
        await DrainChoiceRequestsAsync(host.Services, 1);
        await using (var before = Open()) Assert.Empty(await before.Set<LearningEvidence>().ToListAsync());
        await PostChoiceCallbackAsync(client, 3, right);
        await PostChoiceCallbackAsync(client, 3, right);
        await PostChoiceCallbackAsync(client, 4, right);
        await DrainChoiceRequestsAsync(host.Services, 2);

        await using var saved = Open();
        Assert.Single((await saved.Set<Lesson>().SingleAsync()).Choice!.Attempts);
        Assert.Single(await saved.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(1, probe.Calls);
        Assert.Equal(2, acknowledgements.Ids.Count(id => id == "choice-3"));
    }

    [Fact]
    public async Task Post_ChoiceTextThenNext_PublishesOneNewOpenQuestionAndKeepsRouteEvidenceSeparate()
    {
        await using (var seed = Open())
        {
            var program = LearningProgram.Create(_user, "Понять сложение", "Начинаю", null, "Математика",
                [new("sum", "Сложение", [])], null);
            seed.AddRange(program, LearningContext.Create(program));
            await seed.SaveChangesAsync();
        }
        var generation = new ConcurrentQueue<LessonRequest>();
        var probe = new Probe("")
        {
            Generate = request =>
            {
                generation.Enqueue(request);
                return request.PreferredChoice is null
                    ? new("Новый пример сложения.", "Как вы объясните результат сложения двух отрицательных чисел?")
                    : ChoiceMaterial(request.PreferredChoice.Value);
            }
        };

        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn_next");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, "/learn_next");
        await DrainChoiceRequestsAsync(host.Services, 1);
        Assert.Equal(1, probe.Calls);
        await SendChoiceTextAsync(client, 3, "А");
        await DrainChoiceRequestsAsync(host.Services, 1);
        Assert.Equal(1, probe.Calls);
        await SendChoiceTextAsync(client, 4, "дальше");
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var observer = Open();
        var old = await observer.Set<Lesson>().SingleAsync(lesson => lesson.Status == LessonStatus.Completed);
        var next = await observer.Set<Lesson>().SingleAsync(lesson => lesson.Status == LessonStatus.AwaitingAnswer);
        Assert.Equal(old.ProgramId, next.ProgramId);
        Assert.Equal(old.SkillId, next.SkillId);
        Assert.Null(next.Choice);
        Assert.Contains("Как вы объясните", next.Question);
        Assert.Equal(ChoiceFormat.SingleChoice, generation.First().PreferredChoice);
        Assert.Null(generation.Last().PreferredChoice);
        Assert.Equal(ExerciseEvidenceKind.Understanding, generation.Last().PracticeEvidenceKind);
        Assert.Contains(old.Question, generation.Last().PreviousQuestions!);
        Assert.Single(await observer.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(2, probe.Calls);
    }

    [Theory]
    [InlineData(true, ChoiceFormat.MultipleChoice)]
    [InlineData(false, ChoiceFormat.TrueFalse)]
    public async Task Post_RouteWithDifferentAnswerHistory_SelectsDifferentChoiceFormat(bool priorSuccess, ChoiceFormat expected)
    {
        await using (var seed = Open())
        {
            var program = LearningProgram.Create(_user, "Понять сложение", "Начинаю", null, "Математика",
                [new("root", "Сложение", [])], null);
            var skill = Skill.Create(_user, "Сложение", "Объяснить сложение");
            var previousTopicExercise = Lesson.Start(_user, "Сложение", "Условия", "Объясните правило.");
            previousTopicExercise.LinkToProgram(program, "root");
            previousTopicExercise.LinkToSkill(skill);
            previousTopicExercise.Finish();
            seed.AddRange(program, LearningContext.Create(program), skill, previousTopicExercise);
            for (var index = 0; index < (priorSuccess ? 1 : 2); index++)
            {
                var lesson = Lesson.Start(_user, "Сложение", "Условия", $"Вопрос {index}?");
                lesson.LinkToSkill(skill);
                lesson.Complete("Ответ", "Обратная связь", priorSuccess);
                seed.AddRange(lesson, LearningEvidence.FromAnswer(lesson, _now.AddDays(index - 3), "Test"));
            }
            await seed.SaveChangesAsync();
        }
        var probe = new Probe("") { Generate = request => ChoiceMaterial(request.PreferredChoice!.Value) };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn_next");
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var db = Open();
        var active = await db.Set<Lesson>().SingleAsync(lesson => lesson.Status == LessonStatus.AwaitingAnswer);
        Assert.Equal(expected, active.Choice!.Exercise.Format);
    }

    [Fact]
    public async Task Post_CallbackRetry_RejectsOldButtonsAndAcceptsText()
    {
        var probe = new Probe("") { Material = ChoiceMaterial(ChoiceFormat.SingleChoice) };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn Сложение");
        await DrainChoiceRequestsAsync(host.Services, 1);
        var old = await ChoiceKeyboardAsync();
        await PostChoiceCallbackAsync(client, 2, old.Buttons[1].CallbackData!);
        await DrainChoiceRequestsAsync(host.Services, 1);
        var retry = await ChoiceKeyboardAsync();
        Assert.NotEqual(old.Buttons[0].CallbackData, retry.Buttons[0].CallbackData);
        await PostChoiceCallbackAsync(client, 3, old.Buttons[0].CallbackData!);
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 4, "А");
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var db = Open();
        var saved = (await db.Set<Lesson>().SingleAsync()).Choice!;
        Assert.Equal([ChoiceOutcome.Incorrect, ChoiceOutcome.Correct], saved.Attempts.Select(attempt => attempt.Outcome));
        Assert.Equal([AnswerAssistance.Independent, AnswerAssistance.Assisted], saved.Attempts.Select(attempt => attempt.Assistance));
        Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), message => message.Text.Contains("Сопоставьте", StringComparison.Ordinal));
        Assert.Single(await db.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task Post_ReusedPublishedChoice_RecordsRecognitionWithAssistance()
    {
        var probe = new Probe("") { Material = ChoiceMaterial(ChoiceFormat.SingleChoice) };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn Сложение");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await using (var previous = Open())
        {
            var lesson = await previous.Set<Lesson>().SingleAsync();
            lesson.Finish();
            await previous.SaveChangesAsync();
        }
        await SendChoiceTextAsync(client, 2, "/learn Сложение");
        await DrainChoiceRequestsAsync(host.Services, 1);
        var keyboard = await ChoiceKeyboardAsync();
        await PostChoiceCallbackAsync(client, 3, keyboard.Buttons[0].CallbackData!);
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var db = Open();
        var evidence = Assert.Single(await db.Set<LearningEvidence>().ToListAsync());
        Assert.True(evidence.ChoiceEvidence!.Reused);
        Assert.Equal(AnswerAssistance.Assisted, evidence.Assistance);
    }

    [Fact]
    public async Task Migrate_NullableChoiceColumns_PreservesLegacyOpenLessonAndEvidence()
    {
        Guid lessonId;
        await using (var seed = Open())
        {
            var skill = Skill.Create(_user, "English", "Explain a request");
            var lesson = Lesson.Start(_user, "English", "Read the request", "What does it mean?");
            lesson.LinkToSkill(skill);
            lesson.Complete("Reply", "Correct", true);
            lessonId = lesson.Id;
            seed.AddRange(skill, lesson, LearningEvidence.FromAnswer(lesson, _now, "Legacy"));
            await seed.SaveChangesAsync();
            await seed.GetService<IMigrator>().MigrateAsync("20261120100000_ScopeLearningPracticeAndAnswerReferences");
        }
        await using var db = Open();
        await db.Database.MigrateAsync();
        var restored = await db.Set<Lesson>().SingleAsync();
        var evidence = await db.Set<LearningEvidence>().SingleAsync();
        Assert.Equal(lessonId, restored.Id);
        Assert.Equal(lessonId, evidence.LessonId);
        Assert.Equal("What does it mean?", restored.Question);
        Assert.True(evidence.AssessedCorrect);
        Assert.Null(restored.Choice);
        Assert.Null(evidence.ChoiceEvidence);
    }

    private WebApplicationFactory<Program> ChoiceHost(Probe probe, ChoiceAcknowledgements acknowledgements) =>
        Host(probe, worker: false, publicIngress: true).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ITelegramCallbackResponder>(acknowledgements))));

    private static LessonMaterial ChoiceMaterial(ChoiceFormat format)
    {
        ImmutableArray<ChoiceOption> options = format == ChoiceFormat.TrueFalse
            ? [new(1, "True"), new(2, "False")]
            : [new(1, "Первый вариант"), new(2, "Второй вариант"), new(3, "Третий вариант")];
        const string question = "Выберите подходящий вариант.";
        return new("Рассмотрим условный пример.", question)
        {
            Choice = new(1, format, question, options, format == ChoiceFormat.MultipleChoice ? [1, 2] : [1],
                "Обоснование выбора.", ["Сопоставьте варианты с условием."])
        };
    }

    private async Task<(string Text, InlineKeyboardButton[] Buttons)> ChoiceKeyboardAsync()
    {
        await using var db = Open();
        var reply = await db.TelegramOutboxMessages.Where(message => message.ChatId == _user.Value && message.ReplyMarkupJson != null)
            .OrderByDescending(message => message.DeliveryOrder).FirstAsync();
        var markup = JsonSerializer.Deserialize<InlineKeyboardMarkup>(reply.ReplyMarkupJson!);
        Assert.NotNull(markup);
        return (reply.Text, markup.InlineKeyboard.Select(Assert.Single).ToArray());
    }

    private static async Task DrainChoiceRequestsAsync(IServiceProvider services, int count)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ILearningRequestStore>();
        var processor = scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>();
        for (var index = 0; index < count; index++)
        {
            var lease = await store.ClaimAsync(TimeSpan.FromMinutes(1), default);
            Assert.NotNull(lease);
            Assert.True(await processor.ProcessAsync(lease, 3, default));
        }
        Assert.Null(await store.ClaimAsync(TimeSpan.FromMinutes(1), default));
    }

    private static async Task SendChoiceTextAsync(HttpClient client, int id, string text)
    {
        using var response = await PostPublicAsync(client, id, text);
        response.EnsureSuccessStatusCode();
    }

    private static async Task PostChoiceCallbackAsync(HttpClient client, int update, string data, long owner = 101)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        request.Content = JsonContent.Create(new
        {
            update_id = update,
            callback_query = new
            {
                id = $"choice-{update}",
                from = new { id = owner, is_bot = false, first_name = "Ученик" },
                message = new
                {
                    message_id = 1, date = 1790244000, chat = new { id = owner, type = "private" },
                    from = new { id = 404, is_bot = true, first_name = "OwnDay" }, text = "Упражнение"
                },
                chat_instance = "123456789", data
            }
        });
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private sealed class ChoiceAcknowledgements : ITelegramCallbackResponder
    {
        public ConcurrentQueue<string> Ids { get; } = new();

        public Task AnswerAsync(string callbackId, CancellationToken cancellationToken)
        {
            Ids.Enqueue(callbackId);
            return Task.CompletedTask;
        }
    }
}
