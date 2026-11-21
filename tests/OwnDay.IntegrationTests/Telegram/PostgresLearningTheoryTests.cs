using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Dialogue;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Post_BeginnerContinuesMarketProgram_PublishesClearingExplanationBeforeChoiceQuestion(bool useButton)
    {
        const string goal = "Хочу разобраться в устройстве биржи на примере московской биржи";
        const string use = "Инвестиции";
        const string theory = "Биржа — площадка, где встречаются заявки, то есть предложения купить или продать. " +
            "Когда условия совпадают, покупатель и продавец заключают сделку. Клиринг определяет, что каждый должен передать после сделки. " +
            "Например, если купили две акции (доли в компании) по 100 рублей, покупатель должен передать 200 рублей, а продавец — две акции.";
        const string question = "На организованной площадке заявки покупателя и продавца совпали. Какое действие лучше всего описывает клиринг?";
        const string clarification = "Объясни клиринг проще";
        const string alternative = "Сначала покупатель и продавец договариваются, затем выясняют, кто что должен передать.";
        var probe = new Probe("")
        {
            Curriculum = new("Биржевая торговля", [new("clearing", "Клиринг", [])], null),
            Recognize = input =>
            {
                Assert.Empty(input.Question);
                Assert.NotNull(input.Theory);
                return input.Input == clarification ? LearningIntent.Clarify : LearningIntent.Next;
            },
            Explanation = new(alternative),
            Generate = request =>
            {
                Assert.Equal("Клиринг", request.Topic);
                Assert.Equal(ChoiceFormat.SingleChoice, request.PreferredChoice);
                var program = Assert.IsType<LessonProgramContext>(request.Program);
                Assert.Equal(goal, program.Goal);
                Assert.Contains(use, program.Experience);
                Assert.Equal(use, program.PracticalUse);
                Assert.Equal(DiagnosticStartingLevel.Foundation, program.StartingLevel);
                return new(theory, question)
                {
                    Choice = new(ChoiceExercise.CurrentVersion, ChoiceFormat.SingleChoice, question,
                        [new(1, "Определить, что покупатель и продавец должны передать"), new(2, "Найти подходящую заявку продавца")],
                        [1], "После сделки клиринг определяет, что каждый должен передать.", [])
                };
            }
        };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, goal);
        await SendEnglishAsync(client, 2, use);
        if (useButton)
        {
            await PostChoiceCallbackAsync(client, 3, await ActionButtonAsync(host.Services, LearningActionKind.NoPriorExperience));
            await WaitAllPublicAsync(3);
        }
        else await SendEnglishAsync(client, 3, "новичок");

        await using var db = Open();
        Assert.Equal(goal, (await db.Set<LearningProgram>().SingleAsync()).Goal);
        Assert.Empty(await db.Set<Lesson>().ToListAsync());
        await SendEnglishAsync(client, 4, "Продолжим");
        var pending = await db.Set<Lesson>().AsNoTracking().SingleAsync();
        Assert.True(pending.ExercisePending);
        var replies = await db.TelegramOutboxMessages.Select(message => message.Text).ToListAsync();
        Assert.Contains(replies, text => text.Contains(theory, StringComparison.Ordinal));
        Assert.DoesNotContain(replies, text => text.Contains(question, StringComparison.Ordinal));
        Assert.Empty((await ChoiceKeyboardAsync()).Buttons);

        await SendEnglishAsync(client, 5, clarification);
        var explanation = await ChoiceKeyboardAsync();
        Assert.Contains(alternative, explanation.Text);
        Assert.DoesNotContain(question, explanation.Text);
        Assert.Empty(explanation.Buttons);
        Assert.True((await db.Set<Lesson>().AsNoTracking().SingleAsync()).ExercisePending);
        await SendEnglishAsync(client, 6, "Можно перейти к заданию?");
        var exercise = await ChoiceKeyboardAsync();
        Assert.Contains(question, exercise.Text);
        Assert.Equal(2, exercise.Buttons.Length);
        var opened = await db.Set<Lesson>().AsNoTracking().SingleAsync();
        Assert.Equal(pending.Id, opened.Id);
        Assert.True(opened.AcceptsAnswer);
        Assert.Equal(AnswerAssistance.Independent, opened.Assistance);
        Assert.Empty(probe.AssessedAnswers);
        Assert.Equal(1, probe.KindCalls["generate"]);
    }

    [Fact]
    public async Task Post_OptionalDiagnostic_PublishesLabeledQuestionBeforeGeneratingLesson()
    {
        const string diagnostic = "Как найти общее число фруктов в двух корзинах?";
        var probe = new Probe("")
        {
            Curriculum = new("Математика", [new("sum", "Сложение", [])], diagnostic)
        };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "Хочу научиться складывать числа");
        await SendEnglishAsync(client, 2, "Подсчитывать товары в корзине");
        await SendEnglishAsync(client, 3, "Начинаю с нуля");
        await SendEnglishAsync(client, 4, "давай");

        await using var db = Open();
        var text = await db.TelegramOutboxMessages.OrderByDescending(reply => reply.DeliveryOrder).Select(reply => reply.Text).FirstAsync();
        Assert.StartsWith("Диагностический вопрос", text);
        Assert.Contains(diagnostic, text);
        Assert.Empty(await db.Set<Lesson>().ToListAsync());
        Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        Assert.False(probe.KindCalls.ContainsKey("generate"));

        await SendEnglishAsync(client, 5, "пропустим");
        Assert.True((await db.Set<Lesson>().SingleAsync()).ExercisePending);
        await SendEnglishAsync(client, 6, "пропустить объяснение");
        Assert.True((await db.Set<Lesson>().AsNoTracking().SingleAsync()).AcceptsAnswer);
        Assert.Equal(1, probe.KindCalls["generate"]);
    }

    [Fact]
    public async Task Post_BeginnerRestartsDuringTheory_OpensSameExerciseOnceAndRecordsOnlyBoundAnswer()
    {
        const string goal = "Хочу научиться складывать числа";
        const string use = "Подсчитывать товары в корзине";
        const string theory = "Сложение объединяет количества. Два яблока и одна груша — это три фрукта.";
        const string question = "Сколько фруктов в корзине из трёх яблок и двух груш?";
        var probe = new Probe("")
        {
            Plan = request =>
            {
                Assert.Equal(goal, request.Goal);
                Assert.Contains(use, request.Experience);
                Assert.Contains("Опыт: с нуля", request.Experience);
                return new("Математика", [new("sum", "Сложение", [])], null);
            },
            Generate = request =>
            {
                Assert.Equal("Сложение", request.Topic);
                var program = Assert.IsType<LessonProgramContext>(request.Program);
                Assert.Equal(DiagnosticStartingLevel.Foundation, program.StartingLevel);
                Assert.Equal(goal, program.Goal);
                Assert.Contains(use, program.Experience);
                return new(theory, question);
            }
        };

        Guid lessonId;
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, goal);
            await SendEnglishAsync(client, 2, use);
            var beginnerButton = await ActionButtonAsync(host.Services, LearningActionKind.NoPriorExperience);
            await PostChoiceCallbackAsync(client, 3, beginnerButton);
            await WaitAllPublicAsync(3);
            await SendEnglishAsync(client, 4, "Продолжим");
            await SendEnglishAsync(client, 5, "/answer Пять");

            await using var db = Open();
            var lesson = await db.Set<Lesson>().SingleAsync();
            lessonId = lesson.Id;
            Assert.True(lesson.ExercisePending);
            Assert.Equal("sum", lesson.TopicCode);
            Assert.Equal(DialogueExpectedResponse.None, (await db.Set<DialogueContext>().SingleAsync()).ExpectedResponse);
            var messages = await db.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync();
            Assert.Contains(messages, text => text.Contains(theory, StringComparison.Ordinal));
            Assert.DoesNotContain(messages, text => text.Contains(question, StringComparison.Ordinal));
            Assert.Empty(probe.AssessedAnswers);
            await SendEnglishAsync(client, 6, "Пауза");
        }

        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 7, "Продолжим");
            string callback;
            await using (var db = Open())
            {
                var restored = await db.Set<Lesson>().SingleAsync();
                Assert.Equal(lessonId, restored.Id);
                Assert.True(restored.ExercisePending);
                var replies = await db.TelegramOutboxMessages.OrderBy(reply => reply.DeliveryOrder).ToListAsync();
                Assert.DoesNotContain(replies, reply => reply.Text.Contains(question, StringComparison.Ordinal));
                var keyboard = JsonSerializer.Deserialize<InlineKeyboardMarkup>(replies.Last(reply => reply.ReplyMarkupJson != null).ReplyMarkupJson!)!;
                var button = Assert.Single(keyboard.InlineKeyboard.SelectMany(row => row));
                Assert.Equal("К упражнению", button.Text);
                callback = button.CallbackData!;
            }

            await PostChoiceCallbackAsync(client, 8, callback);
            await WaitAllPublicAsync(8);
            await PostChoiceCallbackAsync(client, 8, callback);
            await SendEnglishAsync(client, 9, "/answer Пять");
        }

        await using var saved = Open();
        var completed = await saved.Set<Lesson>().SingleAsync();
        Assert.Equal(lessonId, completed.Id);
        Assert.Equal(LessonStatus.Completed, completed.Status);
        Assert.False(completed.ExercisePending);
        Assert.Equal(lessonId, Assert.Single(await saved.Set<LearningEvidence>().ToListAsync()).LessonId);
        Assert.Equal(question, Assert.Single(probe.AssessedLessons).Question);
        Assert.Equal(theory, completed.Theory);
        Assert.Equal(1, probe.KindCalls["generate"]);
        Assert.Single(await saved.Set<LearningRequest>().Where(request => request.SourceUpdateId == 8).ToListAsync());
        var messagesAfter = await saved.TelegramOutboxMessages.OrderBy(reply => reply.DeliveryOrder).Select(reply => reply.Text).ToListAsync();
        var exercise = Assert.Single(messagesAfter, text => text.Contains(question, StringComparison.Ordinal));
        Assert.True(messagesAfter.FindIndex(text => text.Contains(theory, StringComparison.Ordinal)) < messagesAfter.IndexOf(exercise));
    }

    [Fact]
    public async Task Post_FocusWithDemonstratedSkill_OffersExerciseWithoutRepeatingTheory()
    {
        var programId = await SeedChoiceProgramAsync();
        await using (var db = Open())
        {
            var program = await db.Set<LearningProgram>().Include(item => item.Routes).SingleAsync();
            var skill = Skill.Create(_user, "Сложение");
            db.Add(skill);
            for (var index = 0; index < 3; index++)
            {
                var lesson = Lesson.Start(_user, "Сложение", "Объяснение", $"Вопрос {index}");
                lesson.LinkToProgram(program, "sum");
                lesson.LinkToSkill(skill);
                lesson.Complete($"Ответ {index}", "Верно", true);
                db.AddRange(lesson, LearningEvidence.FromAnswer(lesson, _now.AddMinutes(index), "test"));
            }
            await db.SaveChangesAsync();
        }

        var probe = new Probe("") { Material = new("Повторное объяснение.", "Сколько будет четыре плюс два?") };
        using var host = DialogueHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "/learn_focus sum");

        await using var saved = Open();
        var active = await saved.Set<Lesson>().SingleAsync(lesson => lesson.Status == LessonStatus.AwaitingAnswer);
        Assert.Equal(programId, active.ProgramId);
        Assert.True(active.AcceptsAnswer);
        Assert.True(active.TheorySkipped);
        await SendEnglishAsync(client, 2, "Продолжим");
        var messages = await saved.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync();
        Assert.DoesNotContain(messages, text => text.Contains(probe.Material.Theory, StringComparison.Ordinal));
        Assert.Contains(messages, text => text.Contains(probe.Material.Question, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_AnswerQueuedDuringTheory_IsRejectedAfterExerciseOpens()
    {
        await SeedChoiceProgramAsync();
        var probe = new Probe("");
        using var host = Host(probe, worker: false, publicIngress: true);
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn_next");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, "/learn_next");
        await SendChoiceTextAsync(client, 3, "/answer Sent before publication");
        await DrainChoiceRequestsAsync(host.Services, 2);

        await using var db = Open();
        Assert.True((await db.Set<Lesson>().SingleAsync()).AcceptsAnswer);
        Assert.Empty(probe.AssessedAnswers);
        Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
    }
}
