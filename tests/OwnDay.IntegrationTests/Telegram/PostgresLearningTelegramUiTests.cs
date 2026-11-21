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
    [Fact]
    public async Task Post_CorrectChoiceWithRepeatedCallbacks_PublishesOneOpenFollowUpAndPreservesRecognition()
    {
        var programId = await SeedChoiceProgramAsync();
        const string question = "Как объяснить правило сложения на примере −3 и −1?";
        var probe = new Probe("")
        {
            Generate = request => request.PreferredChoice is { } format ? ChoiceMaterial(format)
                : OpenChoiceFollowUp(ExerciseEvidenceKind.Understanding, question)
        };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn_next");
        await DrainChoiceRequestsAsync(host.Services, 1);
        var (theory, theoryButtons) = await LearningMessageAsync(1);
        Assert.Contains("Рассмотрим условный пример.", theory);
        Assert.DoesNotContain(ChoiceMaterial(ChoiceFormat.SingleChoice).Question, theory);
        Assert.Equal(["Практика", "Подробнее", "Пример"], theoryButtons.Select(button => button.Text));

        await PostChoiceCallbackAsync(client, 2, theoryButtons[0].CallbackData!);
        await DrainChoiceRequestsAsync(host.Services, 1);
        var (_, choices) = await LearningMessageAsync(2);
        Assert.Equal(["А. Первый вариант", "Б. Второй вариант", "В. Третий вариант"], choices.Take(3).Select(button => button.Text));
        Assert.Equal(1, probe.Calls);

        var correct = choices[0].CallbackData!;
        await PostChoiceCallbackAsync(client, 3, correct);
        await PostChoiceCallbackAsync(client, 3, correct);
        await PostChoiceCallbackAsync(client, 4, correct);
        await DrainChoiceRequestsAsync(host.Services, 2);

        var (feedback, _) = await LearningMessageAsync(3);
        Assert.StartsWith("Верно.", feedback, StringComparison.Ordinal);
        Assert.Contains(question, feedback);
        Assert.DoesNotContain("Текстом — «дальше»", feedback);
        Assert.Contains("кнопка устарела", (await LearningMessageAsync(4)).Text);
        await SendChoiceTextAsync(client, 5, "дай варианты ответа");
        await DrainChoiceRequestsAsync(host.Services, 1);
        Assert.Contains("оставим текущий формат", (await LearningMessageAsync(5)).Text);
        Assert.Equal(2, probe.Calls);
        Assert.Empty(probe.AssessedAnswers);

        await using var db = Open();
        var completed = await db.Set<Lesson>().SingleAsync(lesson => lesson.Status == LessonStatus.Completed);
        var next = await db.Set<Lesson>().SingleAsync(lesson => lesson.Status == LessonStatus.AwaitingAnswer);
        var evidence = Assert.Single(await db.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(ChoiceOutcome.Correct, Assert.Single(completed.Choice!.Attempts).Outcome);
        Assert.Equal(ExerciseEvidenceKind.Recognition, evidence.ChoiceEvidence!.Kind);
        Assert.Equal(SkillMastery.Unknown, LearnerSkillState.Evaluate([evidence]).Mastery);
        Assert.Equal(completed.SkillId, next.SkillId);
        Assert.Equal(programId, next.ProgramId);
        Assert.Null(next.Choice);
        Assert.Equal(next.Id, (await db.Set<DialogueContext>().SingleAsync()).TargetLessonId);
    }

    [Fact]
    public async Task Post_FreeTextAnswer_StartsNewExerciseAfterThreeProviderSteps()
    {
        await SeedChoiceProgramAsync();
        const string firstQuestion = "Почему сумма двух отрицательных чисел отрицательна?";
        const string nextQuestion = "Долг 7 рублей вырос на 4 рубля. Почему новый баланс равен −11?";
        var probe = new Probe("")
        {
            Generate = request => request.PreviousQuestions is null ? OpenChoiceFollowUp(ExerciseEvidenceKind.Understanding, firstQuestion)
                : OpenChoiceFollowUp(ExerciseEvidenceKind.Understanding, nextQuestion),
            Assessment = (_, _) => new("Оба слагаемых уменьшают сумму.", true)
            {
                Rubric = new([new("sum", RubricCriterionResult.Met, "Верно объяснено направление изменения.")])
            }
        };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn_next");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, "к упражнению");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 3, "Оба числа сдвигают сумму влево.");
        await DrainChoiceRequestsAsync(host.Services, 1);

        var (feedback, _) = await LearningMessageAsync(3);
        Assert.Contains("Верно, упражнение зачтено.", feedback);
        Assert.Contains(nextQuestion, feedback);
        await using var db = Open();
        var requestId = await db.Set<LearningRequest>().Where(request => request.SourceUpdateId == 3).Select(request => request.Id).SingleAsync();
        var steps = await db.Set<LearningProviderStep>().Where(step => step.RequestId == requestId)
            .OrderBy(step => step.Ordinal).Select(step => step.Kind).ToArrayAsync();
        Assert.Equal(["intent", "assess", LearningProviderSession.ContinuationStep], steps);
        Assert.Equal(nextQuestion, (await db.Set<Lesson>().SingleAsync(lesson => lesson.Status == LessonStatus.AwaitingAnswer)).Question);
        Assert.Single(await db.Set<LearningEvidence>().ToListAsync());
    }

    [Fact]
    public async Task Post_NextExerciseTimesOut_CommitsChoiceAndOffersRetry()
    {
        await SeedChoiceProgramAsync();
        string correct;
        using (var host = ChoiceHost(new Probe("") { Material = ChoiceMaterial(ChoiceFormat.SingleChoice) }, new()))
        using (var client = host.CreateClient())
        {
            await SendChoiceTextAsync(client, 1, "/learn_next");
            await DrainChoiceRequestsAsync(host.Services, 1);
            await SendChoiceTextAsync(client, 2, "к упражнению");
            await DrainChoiceRequestsAsync(host.Services, 1);
            correct = (await LearningMessageAsync(2)).Buttons[0].CallbackData!;
        }

        var probe = new Probe("generate") { IgnoreCancellation = true };
        using var restarted = ChoiceHost(probe, new()).WithWebHostBuilder(builder =>
            builder.UseSetting("Learning:Processing:ProviderTimeoutSeconds", "2"));
        using var retryClient = restarted.CreateClient();
        try
        {
            await PostChoiceCallbackAsync(retryClient, 3, correct);
            await DrainChoiceRequestsAsync(restarted.Services, 1);
            var (feedback, actions) = await LearningMessageAsync(3);
            Assert.Contains("Верно.", feedback);
            Assert.Contains(actions, button => button.Text == "Дальше");

            await using var db = Open();
            Assert.Equal(LessonStatus.Completed, (await db.Set<Lesson>().SingleAsync()).Status);
            Assert.True((await db.Set<LearningEvidence>().SingleAsync()).AssessedCorrect);
            var step = await db.Set<LearningProviderStep>().SingleAsync(step => step.Kind == LearningProviderSession.ContinuationStep);
            Assert.Equal(LessonProviderFailure.Unavailable, step.Failure);
            Assert.Equal(1, probe.Calls);
        }
        finally { probe.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Post_QuestionAndFormatChangeAfterAnError_PreservesStateAndEndsWithShortSummary()
    {
        await SeedChoiceProgramAsync();
        const string example = "На числовой прямой прибавление отрицательного числа сдвигает точку влево.";
        const string question = "Какая сумма получится, если к −2 прибавить −3?";
        var probe = new Probe("")
        {
            Intent = LearningIntent.Clarify,
            Explanation = new(example),
            Generate = request => request.RequestedFormat == ExerciseResponseFormat.ShortText
                ? OpenChoiceFollowUp(ExerciseEvidenceKind.Recognition, question) : ChoiceMaterial(ChoiceFormat.SingleChoice)
        };
        using (var host = ChoiceHost(probe, new()))
        using (var client = host.CreateClient())
        {
            await SendChoiceTextAsync(client, 1, "/learn_next");
            await DrainChoiceRequestsAsync(host.Services, 1);
            await SendChoiceTextAsync(client, 2, "к упражнению");
            await DrainChoiceRequestsAsync(host.Services, 1);
            var (_, initial) = await LearningMessageAsync(2);
            await PostChoiceCallbackAsync(client, 3, initial[1].CallbackData!);
            await DrainChoiceRequestsAsync(host.Services, 1);
            var (hint, retry) = await LearningMessageAsync(3);
            Assert.Contains("Сопоставьте варианты с условием.", hint);
            Assert.Contains(retry, button => button.Text == "Объяснить");
            Assert.Equal(1, probe.Calls);

            await SendChoiceTextAsync(client, 4, "Что тут происходит с числом на прямой?");
            await DrainChoiceRequestsAsync(host.Services, 1);
            Assert.Contains(example, (await LearningMessageAsync(4)).Text);
            Assert.Single(probe.IntentInputs);
            Assert.Single(probe.ExplanationKinds);
        }

        using (var host = ChoiceHost(probe, new()))
        using (var client = host.CreateClient())
        {
            var (_, oldKeyboard) = await LearningMessageAsync(4);
            await SendChoiceTextAsync(client, 5, "можно короткий ответ?");
            await DrainChoiceRequestsAsync(host.Services, 1);
            Assert.Contains(question, (await LearningMessageAsync(5)).Text);
            await PostChoiceCallbackAsync(client, 6, oldKeyboard[0].CallbackData!);
            await DrainChoiceRequestsAsync(host.Services, 1);
            Assert.Contains("кнопка устарела", (await LearningMessageAsync(6)).Text);
            await SendChoiceTextAsync(client, 7, "пауза");
            await DrainChoiceRequestsAsync(host.Services, 1);
            await SendChoiceTextAsync(client, 8, "Покажи пример");
            await DrainChoiceRequestsAsync(host.Services, 1);
            Assert.Contains(example, (await LearningMessageAsync(8)).Text);
            await SendChoiceTextAsync(client, 9, "на сегодня хватит");
            await DrainChoiceRequestsAsync(host.Services, 1);
            var (summary, _) = await LearningMessageAsync(9);
            Assert.Contains("Тема занятия — Сложение.", summary);
            Assert.Contains("вернёмся к открытому заданию", summary);
            Assert.InRange(summary.Length, 30, 200);
        }

        await using var db = Open();
        var previous = await db.Set<Lesson>().SingleAsync(lesson => lesson.Status == LessonStatus.Finished);
        var current = await db.Set<Lesson>().SingleAsync(lesson => lesson.Status == LessonStatus.Paused);
        Assert.Equal(previous.SkillId, current.SkillId);
        Assert.Equal(previous.ProgramId, current.ProgramId);
        Assert.Single(previous.Choice!.Attempts);
        Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(AnswerAssistance.Assisted, current.Assistance);
        Assert.Equal(LearningSessionEnd.Explicit, (await db.Set<LearningSession>().SingleAsync()).EndReason);
        Assert.Equal(2, probe.KindCalls["generate"]);
    }

    private async Task<(string Text, InlineKeyboardButton[] Buttons)> LearningMessageAsync(int updateId)
    {
        await using var db = Open();
        var requestId = await db.Set<LearningRequest>().Where(request => request.SourceUpdateId == updateId).Select(request => request.Id).SingleAsync();
        var message = await db.TelegramOutboxMessages.Where(reply => reply.LearningRequestId == requestId && reply.EditMessageId == null)
            .OrderByDescending(reply => reply.ReplyOrdinal).FirstAsync();
        var markup = message.ReplyMarkupJson is { } json ? JsonSerializer.Deserialize<InlineKeyboardMarkup>(json) : null;
        return (message.Text, markup?.InlineKeyboard.Select(Assert.Single).ToArray() ?? []);
    }
}
