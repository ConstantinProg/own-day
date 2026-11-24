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
using OwnDay.Application.Dialogue;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Delivery;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task CompletedTopicsAsync_CorrectRecognitionOnBroadTopic_RequiresADeeperCheck()
    {
        const string question = "Name the intermediary who submits an order.";
        var program = LearningProgram.Create(_user, "Understand an exchange", "Beginner", null, "Markets",
            [new("trading", "Execute and explain a trade", [])], null);
        var content = new LearningContent(LearningContent.GeneratedExerciseId, "rubric-v2", LearningContentKind.StructuredExercise,
            "Trading", question, question, new(ExerciseRubric.QuestionScopedVersion, ExerciseEvidenceKind.Recognition,
                [new("role", question)]));
        var lesson = Lesson.Start(_user, "Trading", "A broker submits orders.", question, content: content);
        lesson.LinkToProgram(program, "trading");
        await using var db = Open();
        db.AddRange(program, lesson);
        await db.SaveChangesAsync();
        lesson.ApplyRubricAssessment("Broker", "Correct", new([new("role", RubricCriterionResult.Met, "Named the role.")]), Guid.NewGuid());
        var store = new EfCurriculumStore(db);
        Assert.Empty(await store.CompletedTopicsAsync(_user, program.Id, 1, default));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Empty(await store.CompletedTopicsAsync(_user, program.Id, 1, default));
        Assert.True((await db.Set<Lesson>().SingleAsync()).IsCorrect);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.GetService<IMigrator>().MigrateAsync("20261122120000_BindTelegramLanguagePicker"));
    }

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
            Assert.DoesNotContain("Обоснование", initial.Text);
            Assert.All(buttons, button => Assert.Matches("^a1\\.[A-Za-z0-9_-]{56}$", button.CallbackData!));

            var signed = buttons[0].CallbackData!;
            var tampered = signed[..^1] + (signed[^1] == 'A' ? 'B' : 'A');
            await PostChoiceCallbackAsync(client, 2, tampered);
            await DrainChoiceRequestsAsync(host.Services, 0);
            Assert.Equal(3, (await ChoiceKeyboardAsync()).Buttons.Length);

            await PostChoiceCallbackAsync(client, 3, buttons[0].CallbackData!);
            await PostChoiceCallbackAsync(client, 3, buttons[0].CallbackData!);
            await DrainChoiceRequestsAsync(host.Services, 1);

            var afterToggle = await ChoiceKeyboardAsync();
            Assert.Empty(afterToggle.Text);
            Assert.Equal(1, afterToggle.EditMessageId);
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
            Assert.EndsWith(" — выбрано", restored.Buttons[0].Text);
            Assert.EndsWith(" — выбрано", restored.Buttons[firstOption - 1].Text);
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
        var skillState = LearnerSkillState.Evaluate([evidence]);
        Assert.Equal(SkillMastery.Unknown, skillState.Mastery);
        Assert.Equal(0, skillState.PositiveAssessments);
        Assert.Equal(2, probe.Calls);
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
            Assert.Matches("^a1\\.[A-Za-z0-9_-]{56}$", button.CallbackData!);
        });

        await PostChoiceCallbackAsync(client, 2, right, owner: 202);
        await DrainChoiceRequestsAsync(host.Services, 0);
        await using (var before = Open()) Assert.Empty(await before.Set<LearningEvidence>().ToListAsync());
        await PostChoiceCallbackAsync(client, 3, right);
        await PostChoiceCallbackAsync(client, 3, right);
        await PostChoiceCallbackAsync(client, 4, right);
        await DrainChoiceRequestsAsync(host.Services, 2);

        await using var saved = Open();
        Assert.Single((await saved.Set<Lesson>().SingleAsync()).Choice!.Attempts);
        Assert.Single(await saved.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(2, probe.Calls);
        Assert.Empty(probe.AssessedAnswers);
        Assert.Equal(2, acknowledgements.Ids.Count(id => id == "choice-3"));
    }

    [Theory]
    [InlineData(true, LearningSessionEnd.Idle)]
    [InlineData(false, LearningSessionEnd.Explicit)]
    public async Task Post_ChoiceButtonAfterSessionEnd_ReopensQuestionBeforeAssessing(bool idle, LearningSessionEnd reason)
    {
        await SeedChoiceProgramAsync();
        var probe = new Probe("") { Material = ChoiceMaterial(ChoiceFormat.SingleChoice) };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn_next");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, "/learn_next");
        await DrainChoiceRequestsAsync(host.Services, 1);
        var oldButton = (await ChoiceKeyboardAsync()).Buttons[0].CallbackData!;
        if (idle)
        {
            await using var db = Open();
            var earlier = DateTimeOffset.UtcNow.AddHours(-3);
            Assert.Equal(1, await db.Set<LearningSession>().ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.StartedAt, earlier).SetProperty(session => session.LastActivityAt, earlier)));
        }
        else
        {
            await SendChoiceTextAsync(client, 3, "/learn_end_session");
            await DrainChoiceRequestsAsync(host.Services, 1);
        }

        await PostChoiceCallbackAsync(client, 4, oldButton);
        await DrainChoiceRequestsAsync(host.Services, 1);
        var freshButton = (await ChoiceKeyboardAsync()).Buttons[0].CallbackData!;
        Assert.NotEqual(oldButton, freshButton);
        await PostChoiceCallbackAsync(client, 5, oldButton);
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using (var observer = Open())
        {
            var lesson = await observer.Set<Lesson>().SingleAsync();
            Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
            Assert.Empty(lesson.Choice!.Attempts);
            Assert.Empty(await observer.Set<LearningEvidence>().ToListAsync());
            var sessions = await observer.Set<LearningSession>().ToListAsync();
            Assert.Equal(reason, Assert.Single(sessions, session => session.EndedAt is not null).EndReason);
            Assert.Single(sessions, session => session.EndedAt is null);
        }

        await PostChoiceCallbackAsync(client, 6, freshButton);
        await DrainChoiceRequestsAsync(host.Services, 1);
        await using var completed = Open();
        Assert.Equal(LessonStatus.Completed, (await completed.Set<Lesson>().SingleAsync()).Status);
        Assert.Single(await completed.Set<LearningEvidence>().ToListAsync());
    }

    [Theory]
    [InlineData("Z")]
    [InlineData("А, Б")]
    [InlineData("А, А")]
    public async Task Post_InvalidChoiceText_ReturnsOptionsAndAcceptsCorrectedAnswer(string invalid)
    {
        var probe = new Probe("") { Material = ChoiceMaterial(ChoiceFormat.SingleChoice), Intent = LearningIntent.Ambiguous };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn Сложение");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, invalid);
        await DrainChoiceRequestsAsync(host.Services, 1);
        await using (var observer = Open())
        {
            Assert.Empty((await observer.Set<Lesson>().SingleAsync()).Choice!.Attempts);
            Assert.Contains(await observer.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync(),
                text => text.Contains("Выберите допустимый", StringComparison.Ordinal));
        }
        await SendChoiceTextAsync(client, 3, "А");
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var completed = Open();
        Assert.Single((await completed.Set<Lesson>().SingleAsync()).Choice!.Attempts);
        Assert.Empty(probe.IntentInputs);
    }

    [Theory]
    [InlineData("А")]
    [InlineData("/answer А")]
    public async Task Post_ChoiceTextWhileClarificationIsPending_AssessesNewSelection(string answer)
    {
        var probe = new Probe("") { Material = ChoiceMaterial(ChoiceFormat.SingleChoice) };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn Сложение");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, "не знаю");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await using (var observer = Open())
            Assert.NotNull((await observer.Set<DialogueContext>().SingleAsync()).PendingClarification);

        await SendChoiceTextAsync(client, 3, answer);
        await DrainChoiceRequestsAsync(host.Services, 1);
        await using var completed = Open();
        Assert.Single((await completed.Set<Lesson>().SingleAsync()).Choice!.Attempts);
    }

    [Fact]
    public async Task Post_CorrectChoiceText_PublishesOneNewOpenQuestionAndKeepsRouteEvidenceSeparate()
    {
        await SeedChoiceProgramAsync();
        var generation = new ConcurrentQueue<LessonRequest>();
        var probe = new Probe("")
        {
            Generate = request =>
            {
                generation.Enqueue(request);
                return request.PreferredChoice is null
                    ? OpenChoiceFollowUp(ExerciseEvidenceKind.Understanding, "Как вы объясните результат сложения двух отрицательных чисел?")
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

    [Fact]
    public async Task Post_RecognitionGeneratedAfterChoice_RejectsFollowUpAndKeepsTopicOpen()
    {
        var programId = await SeedChoiceProgramAsync();
        var probe = new Probe("")
        {
            Generate = request => request.PreferredChoice is { } format ? ChoiceMaterial(format)
                : OpenChoiceFollowUp(ExerciseEvidenceKind.Recognition, "Назовите знак сложения.")
        };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn_next");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, "/learn_next");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 3, "А");
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var observer = Open();
        Assert.NotNull(Assert.Single(await observer.Set<Lesson>().ToListAsync()).Choice);
        Assert.Empty(await new EfCurriculumStore(observer).CompletedTopicsAsync(_user, programId, 1, default));
        Assert.Single(await observer.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(2, probe.KindCalls["generate"]);
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
        Assert.Equal(2, probe.Calls);
    }

    [Theory]
    [InlineData("В", "заключена ли сделка", "момент исполнения")]
    [InlineData("Г", "момент исполнения", "заключена ли сделка")]
    public async Task Post_WrongClearingSelection_TargetsTheSelectedDistinction(string answer, string distinction, string otherDistinction)
    {
        var probe = new Probe("") { Material = ClearingChoiceMaterial() };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn Клиринг");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, answer);
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var db = Open();
        var lesson = await db.Set<Lesson>().SingleAsync();
        var attempt = Assert.Single(lesson.Choice!.Attempts);
        Assert.Equal(ChoiceOutcome.Incorrect, attempt.Outcome);
        Assert.False(lesson.Choice.SolutionSeen);
        Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        var replies = await db.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync();
        Assert.Contains(replies, reply => reply.Contains(distinction, StringComparison.Ordinal));
        Assert.DoesNotContain(replies, reply => reply.Contains(otherDistinction, StringComparison.Ordinal));
        Assert.DoesNotContain(replies, reply => reply.Contains("Правильный выбор", StringComparison.Ordinal));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task Post_RepeatedClearingErrorsAcrossRestarts_WaitsForExplicitNextAfterExplaining()
    {
        const string check = "Инвестор купил облигацию. Объясните, зачем фиксировать обязательства сторон до расчётов.";
        var generation = new ConcurrentQueue<LessonRequest>();
        var probe = new Probe("")
        {
            Generate = request =>
            {
                generation.Enqueue(request);
                return request.PreferredChoice is null
                    ? OpenChoiceFollowUp(ExerciseEvidenceKind.Understanding, check) : ClearingChoiceMaterial();
            },
            Assessment = (_, _) => new("Вы объяснили, зачем отделять обязательства от расчётов.", true)
            {
                Rubric = new([new("sum", RubricCriterionResult.Met, "Объяснено различие этапов.")])
            }
        };
        await using (var seed = Open())
        {
            var program = LearningProgram.Create(_user, "Разобраться в устройстве Московской биржи",
                "Применение: Инвестиции\nОпыт: Начинаю с нуля", null, "Финансы", [new("clearing", "Клиринг", [])], null);
            seed.AddRange(program, LearningContext.Create(program));
            await seed.SaveChangesAsync();
        }
        using (var host = ChoiceHost(probe, new()))
        using (var client = host.CreateClient())
        {
            await SendChoiceTextAsync(client, 1, "/learn_next");
            await DrainChoiceRequestsAsync(host.Services, 1);
            await SendChoiceTextAsync(client, 2, "к упражнению");
            await DrainChoiceRequestsAsync(host.Services, 1);
            var prompt = await ChoiceKeyboardAsync();
            await PostChoiceCallbackAsync(client, 3, prompt.Buttons[2].CallbackData!);
            await DrainChoiceRequestsAsync(host.Services, 1);
        }

        using (var host = ChoiceHost(probe, new()))
        using (var client = host.CreateClient())
        {
            await SendChoiceTextAsync(client, 4, "Г");
            await DrainChoiceRequestsAsync(host.Services, 1);
            await using (var db = Open())
            {
                var saved = await db.Set<Lesson>().SingleAsync();
                Assert.Equal(new[] { 3, 4 }, saved.Choice!.Attempts.Select(attempt => Assert.Single(attempt.Selected)).ToArray());
                var guidance = await db.Set<LessonInteraction>().OrderBy(item => item.Id).Select(item => item.Explanation).ToListAsync();
                Assert.Equal(2, guidance.Count);
                Assert.Contains("заключена ли сделка", guidance[0]);
                Assert.Contains("накладной", guidance[1]);
                Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
            }
            await SendChoiceTextAsync(client, 5, "Г");
            await DrainChoiceRequestsAsync(host.Services, 1);
            await using (var db = Open())
            {
                var lesson = await db.Set<Lesson>().SingleAsync(item => item.Choice != null);
                Assert.Equal(3, lesson.Choice!.Attempts.Length);
                Assert.True(lesson.Choice.SolutionSeen);
                Assert.Contains("обязательства", (await db.Set<LessonInteraction>().SingleAsync(item =>
                    item.Kind == LessonInteractionKind.FullAnswer)).Explanation);
                var evidence = await db.Set<LearningEvidence>().SingleAsync();
                var state = LearnerSkillState.Evaluate([evidence]);
                Assert.Equal(SkillMastery.NeedsPractice, state.Mastery);
                Assert.Equal(0, state.PositiveAssessments);
                Assert.Empty(await new EfCurriculumStore(db).CompletedTopicsAsync(_user, lesson.ProgramId!.Value, lesson.RouteVersion!.Value, default));
                Assert.Single(await db.Set<Lesson>().ToListAsync());
                Assert.Single(generation);
            }
            await SendChoiceTextAsync(client, 6, "дальше");
            await DrainChoiceRequestsAsync(host.Services, 1);
            await using (var db = Open())
            {
                var next = await db.Set<Lesson>().SingleAsync(item => item.Choice == null);
                Assert.True(next.AcceptsAnswer);
                Assert.Equal(check, next.Question);
            }
            await SendChoiceTextAsync(client, 7, "/answer Сначала фиксируют, кто должен деньги и кто должен облигацию. Потом исполняют эти обязательства.");
            await DrainChoiceRequestsAsync(host.Services, 1);
        }

        await using var final = Open();
        var fresh = await final.Set<Lesson>().SingleAsync(lesson => lesson.Choice == null);
        Assert.Equal(check, fresh.Question);
        Assert.True(fresh.IsCorrect);
        Assert.Equal(AnswerAssistance.Independent, fresh.Assistance);
        var observations = await final.Set<LearningEvidence>().ToListAsync();
        Assert.Equal(1, LearnerSkillState.Evaluate(observations).PositiveAssessments);
        Assert.Contains("clearing", await new EfCurriculumStore(final).CompletedTopicsAsync(_user, fresh.ProgramId!.Value, fresh.RouteVersion!.Value, default));
        Assert.Equal(3, generation.Count);
        Assert.All(generation, request =>
        {
            Assert.Equal("Инвестиции", request.Program?.PracticalUse);
            Assert.Equal(DiagnosticStartingLevel.Foundation, request.Program?.StartingLevel);
        });
        Assert.Contains(ClearingChoiceMaterial().Question, generation.ElementAt(1).PreviousQuestions!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_ExplicitChoiceSolutionBeforeAttempt_OpensIndependentQuestionAndRecordsNoChoiceEvidence(bool bounded)
    {
        const string use = "Checking a purchase total";
        await SeedChoiceProgramAsync($"Application: {use}\nExperience: Starting from scratch");
        var probe = new Probe("")
        {
            Generate = request =>
            {
                Assert.Equal(use, request.Program?.PracticalUse);
                return request.PreferredChoice is null
                    ? OpenChoiceFollowUp(ExerciseEvidenceKind.Understanding, "Как проверить сумму на новом примере?")
                    : ChoiceMaterial(request.PreferredChoice.Value);
            }
        };
        using var host = ChoiceHost(probe, new()).WithWebHostBuilder(builder =>
            builder.UseSetting("Learning:Processing:PracticeEnabled", "true"));
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, bounded ? "давай два упражнения" : "/learn_next");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, "к упражнению");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 3, "покажи решение");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 4, "дальше");
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var db = Open();
        var shown = await db.Set<Lesson>().SingleAsync(lesson => lesson.Choice != null);
        Assert.Equal(LessonStatus.Finished, shown.Status);
        Assert.True(shown.Choice!.SolutionSeen);
        Assert.Empty(shown.Choice.Attempts);
        Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        var fresh = await db.Set<Lesson>().SingleAsync(lesson => lesson.Choice == null && lesson.Status == LessonStatus.AwaitingAnswer);
        Assert.True(fresh.AcceptsAnswer);
        if (bounded)
        {
            var practice = (await db.Set<LearningContext>().SingleAsync()).Practice!;
            Assert.Equal(fresh.Id, Assert.Single(practice.LessonIds));
            Assert.Equal(LearningPracticeStatus.Running, practice.Status);
        }
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task Post_RevealedChoiceReplacementFails_RetryButtonReplacesTheActivePracticeExercise()
    {
        await SeedChoiceProgramAsync();
        var openAttempts = 0;
        var probe = new Probe("")
        {
            Generate = request =>
            {
                if (request.PreferredChoice is { } format) return ChoiceMaterial(format);
                if (Interlocked.Increment(ref openAttempts) == 1) throw new LessonProviderException();
                return OpenChoiceFollowUp(ExerciseEvidenceKind.Understanding, "Как проверить сумму на новом примере?");
            }
        };
        using var host = ChoiceHost(probe, new()).WithWebHostBuilder(builder =>
            builder.UseSetting("Learning:Processing:PracticeEnabled", "true"));
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "давай два упражнения");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, "к упражнению");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 3, "покажи решение");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 4, "дальше");
        await DrainChoiceRequestsAsync(host.Services, 1);

        string callback;
        Guid revealedId;
        await using (var failed = Open())
        {
            var revealed = await failed.Set<Lesson>().SingleAsync();
            revealedId = revealed.Id;
            Assert.True(revealed.Choice!.SolutionSeen);
            Assert.True(revealed.AcceptsAnswer);
            Assert.Equal(revealedId, Assert.Single((await failed.Set<LearningContext>().SingleAsync()).Practice!.LessonIds));
            var reply = await failed.TelegramOutboxMessages.SingleAsync(message => message.Text.Contains("новое упражнение пока недоступно"));
            var markup = JsonSerializer.Deserialize<InlineKeyboardMarkup>(Assert.IsType<string>(reply.ReplyMarkupJson));
            var button = Assert.Single(Assert.Single(Assert.IsType<InlineKeyboardMarkup>(markup).InlineKeyboard));
            Assert.Equal("Дальше", button.Text);
            callback = Assert.IsType<string>(button.CallbackData);
        }

        await PostChoiceCallbackAsync(client, 5, callback);
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var final = Open();
        Assert.Equal(LessonStatus.Finished, (await final.Set<Lesson>().FindAsync(revealedId))!.Status);
        var fresh = await final.Set<Lesson>().SingleAsync(lesson => lesson.Choice == null);
        Assert.True(fresh.AcceptsAnswer);
        Assert.Equal(fresh.Id, Assert.Single((await final.Set<LearningContext>().SingleAsync()).Practice!.LessonIds));
        Assert.Equal(2, openAttempts);
    }

    [Fact]
    public async Task Post_ExplicitChoiceExample_GivesExplanationWithVerificationNoticeWithoutAnAttempt()
    {
        var probe = new Probe("")
        {
            Material = ClearingChoiceMaterial(),
            Recognize = _ => LearningIntent.Clarify,
            Explanation = new("На складе сначала записывают, сколько коробок должен передать каждый участник.")
            {
                Provenance = new("test", null, null, null, null, MaterialSourceStatus.Unverified)
            }
        };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn Клиринг");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, "Приведи ещё один пример и объясни подробнее");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 3, "Продолжим");
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var db = Open();
        Assert.Equal(1, probe.KindCalls["explain"]);
        var choice = (await db.Set<Lesson>().SingleAsync()).Choice!;
        Assert.Empty(choice.Attempts);
        Assert.Equal(1, choice.HintsUsed);
        var explanation = await db.Set<LessonInteraction>().SingleAsync();
        Assert.Contains("коробок", explanation.Explanation);
        Assert.Equal(MaterialSourceStatus.Unverified, explanation.Provenance?.Status);
        Assert.Equal(2, await db.TelegramOutboxMessages.CountAsync(reply =>
            reply.Text.Contains("Актуальные детали не удалось подтвердить")));
    }

    [Fact]
    public async Task Post_ChoiceAnswerInOwnWords_ClarifiesAmbiguityAndGradesOnlyTheInterpretedSelection()
    {
        const string ambiguous = "Клиринг как-то обрабатывает сделку";
        const string answer = "Посчитать, кто сколько должен после сделки";
        var interpreted = new ConcurrentQueue<string>();
        var probe = new Probe("")
        {
            Material = ClearingChoiceMaterial(),
            Interpretation = input =>
            {
                Assert.Equal(ChoiceFormat.SingleChoice, input.Format);
                Assert.Equal(4, input.Options.Length);
                interpreted.Enqueue(input.Answer);
                return new(input.Answer == answer ? [1] : null);
            }
        };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn Клиринг");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, ambiguous);
        await DrainChoiceRequestsAsync(host.Services, 1);
        await using (var db = Open())
        {
            Assert.Empty((await db.Set<Lesson>().SingleAsync()).Choice!.Attempts);
            Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
            Assert.Contains(await db.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync(),
                text => text.Contains("Уточните", StringComparison.Ordinal));
        }
        await SendChoiceTextAsync(client, 3, answer);
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var final = Open();
        var assessment = Assert.Single((await final.Set<Lesson>().SingleAsync()).Choice!.Attempts);
        Assert.Equal(answer, assessment.Answer);
        Assert.Equal(ChoiceOutcome.Correct, assessment.Outcome);
        Assert.True((await final.Set<LearningEvidence>().SingleAsync()).DeterministicIsCorrect);
        Assert.Equal(new[] { ambiguous, answer }, interpreted.ToArray());
        Assert.Empty(probe.AssessedAnswers);
    }

    [Fact]
    public async Task Post_RepeatedOpenAnswer_ChangesGuidanceAndExplainsWithoutCredit()
    {
        const string explanation = "Клиринг фиксирует обязательства сторон до того, как расчёты их исполняют.";
        const string hint = "Посмотрите, что уже известно до передачи актива.";
        var probe = new Probe("")
        {
            Material = OpenChoiceFollowUp(ExerciseEvidenceKind.Understanding, "Объясните, чем клиринг отличается от расчётов."),
            Assessment = (lesson, _) => new(lesson.AnswerHelp == TeachingAction.Explain ? explanation : hint, false)
            {
                Rubric = new([new("sum", RubricCriterionResult.NotMet, "Разница между этапами не показана.")]),
                Correction = new(ExerciseCorrectionKind.Hint, hint)
            }
        };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn Клиринг");
        await DrainChoiceRequestsAsync(host.Services, 1);
        for (var id = 2; id <= 4; id++)
        {
            await SendChoiceTextAsync(client, id, "/answer Это сопоставление заявок.");
            await DrainChoiceRequestsAsync(host.Services, 1);
        }

        await using var db = Open();
        var lesson = await db.Set<Lesson>().SingleAsync();
        Assert.Equal(new TeachingAction?[] { TeachingAction.Hint, TeachingAction.Simplify, TeachingAction.Explain },
            probe.AssessedLessons.Select(input => input.AnswerHelp).ToArray());
        var corrections = lesson.RubricEvaluation!.Corrections;
        Assert.Equal(2, corrections.Length);
        Assert.NotEqual(corrections[0].Proposal.Guidance, corrections[1].Proposal.Guidance);
        Assert.Equal(ExerciseCorrectionKind.Simplify, corrections[1].Proposal.Kind);
        Assert.Contains(explanation, lesson.Feedback);
        Assert.False((await db.Set<LearningEvidence>().SingleAsync()).AssessedCorrect);
    }

    private static LessonMaterial ClearingChoiceMaterial()
    {
        const string question = "Заявки на бирже совпали. Какое действие лучше всего описывает клиринг?";
        return new("Сделка, клиринг и расчёты происходят на разных этапах.", question)
        {
            Choice = new(ChoiceExercise.CurrentVersion, ChoiceFormat.SingleChoice, question,
                [new(1, "Определить обязательства по сделке"), new(2, "Подобрать инвестицию с гарантированной прибылью"),
                 new(3, "Сопоставить заявки"), new(4, "Перевести деньги и передать актив")], [1],
                "Клиринг определяет обязательства по уже заключённой сделке. Расчёты исполняют их.", [])
            {
                Guidance =
                [
                    new(2, "Проверьте, спрашивают ли о выборе инвестиции или об обработке сделки.", "Представьте, что покупку уже согласовали."),
                    new(3, "Проверьте, заключена ли сделка к моменту действия, о котором спрашивают.", "Сравните момент согласования заказа и обработку уже принятого заказа."),
                    new(4, "Сравните запись о том, что нужно передать, и момент исполнения договорённости.", "Сравните список товаров в накладной и их фактическую доставку.")
                ]
            }
        };
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

    private async Task<Guid> SeedChoiceProgramAsync(string experience = "Начинаю")
    {
        await using var seed = Open();
        var program = LearningProgram.Create(_user, "Понять сложение", experience, null, "Математика",
            [new("sum", "Сложение", [])], null);
        seed.AddRange(program, LearningContext.Create(program));
        await seed.SaveChangesAsync();
        return program.Id;
    }

    private static LessonMaterial OpenChoiceFollowUp(ExerciseEvidenceKind kind, string question) =>
        new("Новый пример сложения.", question)
        {
            Content = new(LearningContent.GeneratedExerciseId, "rubric-v2", LearningContentKind.StructuredExercise,
                "Сложение", question, "Оцените опубликованный вопрос.", new(ExerciseRubric.QuestionScopedVersion, kind, [new("sum", question)]))
        };

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

    private async Task<(string Text, InlineKeyboardButton[] Buttons, int? EditMessageId)> ChoiceKeyboardAsync()
    {
        await using var db = Open();
        var reply = await db.TelegramOutboxMessages.Where(message => message.ChatId == _user.Value && message.ReplyMarkupJson != null)
            .OrderByDescending(message => message.DeliveryOrder).FirstAsync();
        var markup = JsonSerializer.Deserialize<InlineKeyboardMarkup>(reply.ReplyMarkupJson!);
        Assert.NotNull(markup);
        var buttons = markup.InlineKeyboard.Select(Assert.Single)
            .Where(button => button.Text is "Ответить" or "Submit" || button.Text.Length > 2 && button.Text[1] == '.').ToArray();
        return (reply.Text, buttons, reply.EditMessageId);
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
        using var response = await PostPublicAsync(client, id, text, clientLanguage: "ru");
        response.EnsureSuccessStatusCode();
    }

    private static async Task PostChoiceCallbackAsync(HttpClient client, int update, string data, long owner = 101, string clientLanguage = "ru")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        request.Content = JsonContent.Create(new
        {
            update_id = update,
            callback_query = new
            {
                id = $"choice-{update}",
                from = new { id = owner, is_bot = false, first_name = "Ученик", language_code = clientLanguage },
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
