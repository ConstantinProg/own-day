using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OwnDay.Application.Dialogue;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Learning;
using OwnDay.Infrastructure.Telegram.Delivery;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_RussianExerciseAcrossSessionEndAndRestart_PreservesLanguageRequirementsAndAudit()
    {
        const string first = "Назовите ограничение лимитной заявки.";
        const string second = "Приведите короткий пример.";
        const string answer = "A buy limit order sets a maximum price, for example 100.";
        var rubric = new ExerciseRubric(ExerciseRubric.QuestionScopedVersion, ExerciseEvidenceKind.Understanding,
            [new("limit", first), new("example", second)]);
        var probe = new Probe("")
        {
            Curriculum = new("Биржа", [new("orders", "Биржевые заявки", [])], "Кто принимает поручение инвестора?"),
            Material = new("Заявки покупателей и продавцов встречаются на бирже.", rubric.QuestionText())
            {
                Provenance = new("OpenAI:recorded-test-model", null, null, null, null, MaterialSourceStatus.None),
                Content = new(LearningContent.GeneratedExerciseId, "rubric-v2", LearningContentKind.StructuredExercise,
                    "Объяснить лимитную заявку", first, "Требования задания", rubric)
            },
            Recognize = input => input.Input switch
            {
                "Объясни проще" or "Дай подсказку" => LearningIntent.Clarify,
                "Какие требования к ответу?" => LearningIntent.ShowCriteria,
                "Закончим на сегодня" => LearningIntent.EndSession,
                "Почему мой ответ неправильный?" => LearningIntent.ExplainAssessment,
                "Вернёмся к предыдущей теме" => LearningIntent.PreviousTopic,
                _ => LearningIntent.Answer
            },
            Explanation = new("Вспомните, что при покупке можно ограничить цену сверху."),
            Assessment = (_, _) => new("Вы обозначили цену и привели пример.", true)
            {
                Source = "OpenAI:recorded-assessment-model",
                Rubric = new([new("limit", RubricCriterionResult.Met, "Указана максимальная цена."),
                    new("example", RubricCriterionResult.Met, "Есть числовой пример.")])
            }
        };
        Guid lessonId;
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            string[] turns = ["Хочу изучить биржу", "инвистиции", "Начинаю с нуля", "пропустить", "К упражнению",
                "Какие требования к ответу?", "Объясни проще", "Дай подсказку", "Закончим на сегодня"];
            for (var index = 0; index < turns.Length; index++)
                await SendEnglishAsync(client, index + 1, turns[index]);
            await using var db = Open();
            var exercise = await db.Set<Lesson>().Include(item => item.Interactions).SingleAsync();
            lessonId = exercise.Id;
            Assert.Equal(2, exercise.Interactions.Count);
            Assert.Null(exercise.Answer);
            Assert.Equal(LearningSessionEnd.Explicit, (await db.Set<LearningSession>().SingleAsync()).EndReason);
            Assert.True((await db.Set<LearningProgram>().SingleAsync()).SubjectDiagnostic!.Deferred);
        }

        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 10, "Продолжим");
            await SendEnglishAsync(client, 11, answer);
            await SendEnglishAsync(client, 12, "Почему мой ответ неправильный?");
            await SendEnglishAsync(client, 13, "Вернёмся к предыдущей теме");
        }

        await using var result = Open();
        var lesson = await result.Set<Lesson>().SingleAsync();
        Assert.Equal(lessonId, lesson.Id);
        Assert.Equal(answer, lesson.Answer);
        Assert.Equal(answer, Assert.Single(probe.AssessedAnswers));
        Assert.Equal("OpenAI:recorded-test-model", lesson.Provenance!.Generator);
        var evidence = await result.Set<LearningEvidence>().SingleAsync();
        Assert.Equal("OpenAI:recorded-assessment-model", evidence.AssessmentSource);
        Assert.Equal(new[] { first, second }, evidence.Content!.StructuredRubric!.Criteria.Select(item => item.Requirement));
        Assert.Equal(2, await result.Set<LearningSession>().CountAsync());
        var replies = await result.TelegramOutboxMessages.Select(message => message.Text).ToListAsync();
        Assert.Contains(replies, message => message.Contains(rubric.QuestionText(), StringComparison.Ordinal));
        Assert.Contains(replies, message => message.Contains("На что обратить внимание", StringComparison.Ordinal) &&
            message.Contains(rubric.QuestionText(), StringComparison.Ordinal));
        Assert.Contains(replies, message => message.Contains("Напишите ответ", StringComparison.Ordinal));
        Assert.Contains(replies, message => message.Contains("Указана максимальная цена", StringComparison.Ordinal));
        Assert.Contains(replies, message => message.Contains("Предыдущая тема", StringComparison.Ordinal));
        Assert.DoesNotContain(replies, message => message.Contains("recorded-", StringComparison.Ordinal) ||
            message.Contains("NormalizedText", StringComparison.Ordinal) || message.Contains("/learn_ask", StringComparison.Ordinal) ||
            message.Contains("Write your answer", StringComparison.Ordinal) || message.Contains("не удалось подтвердить", StringComparison.Ordinal));
        Assert.Equal(2, probe.KindCalls["generate"]);
        Assert.Equal(1, probe.KindCalls["assess"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_WholeEnglishJourneyAndHostedRestart_RestoresSameQuestionAndHelpWithoutCommands(bool complete)
    {
        var probe = new Probe("")
        {
            Recognize = input => input.Input == "Почему здесь используется could?" ? LearningIntent.Clarify : LearningIntent.Answer
        };
        var sender = new ExperienceSender();
        Guid lessonId;
        string question;
        var id = 0;
        CancellationToken stopped;
        using (var host = ExperienceHost(probe, sender))
        using (var client = host.CreateClient())
        {
            Assert.Contains(host.Services.GetServices<IHostedService>(), service => service is LearningRequestWorker);
            Assert.Contains(host.Services.GetServices<IHostedService>(), service => service is TelegramOutboxWorker);
            stopped = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped;
            await SendEnglishAsync(client, ++id, "Хочу читать и писать на английском");
            await SendEnglishAsync(client, ++id, "Рабочие письма");
            await SendEnglishAsync(client, ++id, "Понимаю отдельные слова");
            if (complete)
            {
                await SendEnglishAsync(client, ++id, "да");
                await SendEnglishAsync(client, ++id, "B");
                await SendEnglishAsync(client, ++id, "Yes, I can send the list today.");
            }
            else await SendEnglishAsync(client, ++id, "пропустим");
            await SendEnglishAsync(client, ++id, "К упражнению");
            await SendEnglishAsync(client, ++id, "Почему здесь используется could?");
            await using (var db = Open())
            {
                var lesson = await db.Set<Lesson>().Include(value => value.Interactions).SingleAsync();
                lessonId = lesson.Id;
                question = lesson.Question;
                Assert.Single(lesson.Interactions);
                Assert.Equal(AnswerAssistance.Assisted, lesson.Assistance);
            }
            await SendEnglishAsync(client, ++id, "Мне пора, продолжим позже");
            await using (var db = Open())
            {
                Assert.Equal(LessonStatus.Paused, (await db.Set<Lesson>().SingleAsync()).Status);
                Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
            }
            await WaitExperienceDeliveryAsync();
        }
        Assert.True(stopped.IsCancellationRequested);
        var calls = probe.Calls;
        using (var restarted = ExperienceHost(probe, sender))
        using (var client = restarted.CreateClient())
        {
            // Redelivery across a real stopped/started Host cannot repeat effects.
            using var duplicate = await PostPublicAsync(client, id, "Мне пора, продолжим позже");
            duplicate.EnsureSuccessStatusCode();
            await SendEnglishAsync(client, ++id, "Продолжим");
            await SendEnglishAsync(client, ++id, "помощь");
            await WaitExperienceDeliveryAsync();
        }
        await using var result = Open();
        var restored = await result.Set<Lesson>().Include(value => value.Interactions).SingleAsync();
        Assert.Equal(lessonId, restored.Id);
        Assert.Equal(question, restored.Question);
        Assert.Equal(LessonStatus.AwaitingAnswer, restored.Status);
        Assert.Equal(AnswerAssistance.Assisted, restored.Assistance);
        Assert.Single(restored.Interactions);
        Assert.Equal(calls, probe.Calls);
        Assert.Equal(0, probe.KindCalls.GetValueOrDefault("generate"));
        Assert.Equal(complete ? 1 : 0, probe.KindCalls.GetValueOrDefault("assess"));
        Assert.Empty(await result.InboxItems.ToListAsync());
        Assert.Empty(await result.Actions.ToListAsync());
        Assert.Empty(await result.Projects.ToListAsync());
        Assert.Empty(await result.Set<LearningEvidence>().ToListAsync());
        Assert.Empty(await result.Set<StudySchedule>().ToListAsync());
        Assert.Equal("Хочу читать и писать на английском", (await result.Set<LearningProgram>().SingleAsync()).Goal);
        Assert.Equal(DialogueExpectedResponse.LessonAnswer, (await result.Set<DialogueContext>().SingleAsync()).ExpectedResponse);
        Assert.Equal(id, await result.Set<LearningRequest>().CountAsync());
        Assert.All(await result.Set<LearningRequest>().ToListAsync(), request => Assert.Equal(LearningRequestStatus.Completed, request.Status));
        var delivered = sender.Messages.ToArray();
        Assert.Contains(delivered, message => message.Contains("Ранее", StringComparison.Ordinal) && message.Contains("Explanation", StringComparison.Ordinal));
        Assert.Contains(question, delivered[^1]);
        Assert.All(delivered, message => Assert.InRange(message.Length, 1, 4096));
    }

    [Fact]
    public async Task Stop_HostDuringExplanation_RecoversAcceptedRequestsAndKeepsQuestionUnassessed()
    {
        var probe = new Probe("explain") { Intent = LearningIntent.Clarify };
        Guid lessonId;
        using (var host = ExperienceHost(probe, new ExperienceSender()))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
            await SendEnglishAsync(client, 2, "Рабочие письма");
            await SendEnglishAsync(client, 3, "Начинаю с нуля");
            await SendEnglishAsync(client, 4, "пропустим");
            await SendEnglishAsync(client, 5, "К упражнению");
            await using (var db = Open()) lessonId = (await db.Set<Lesson>().SingleAsync()).Id;
            using var explain = await PostPublicAsync(client, 6, "Объясни could");
            explain.EnsureSuccessStatusCode();
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var pause = await PostPublicAsync(client, 7, "пауза").WaitAsync(TimeSpan.FromSeconds(3));
            pause.EnsureSuccessStatusCode();
            // Disposing the Host cancels the actual provider invocation and worker.
        }
        await using (var db = Open())
            await db.Database.ExecuteSqlRawAsync("UPDATE learning_requests SET lease_until = clock_timestamp() - interval '1 second' WHERE status = 2");
        var recovery = new Probe("") { Intent = LearningIntent.Clarify };
        using (var restarted = ExperienceHost(recovery, new ExperienceSender()))
        using (var client = restarted.CreateClient())
        {
            await WaitAllPublicAsync(7);
            await SendEnglishAsync(client, 8, "Продолжим");
            await WaitExperienceDeliveryAsync();
        }
        await using var result = Open();
        Assert.Equal(lessonId, (await result.Set<Lesson>().SingleAsync()).Id);
        Assert.Equal(LessonStatus.AwaitingAnswer, (await result.Set<Lesson>().SingleAsync()).Status);
        Assert.Empty(await result.Set<LearningEvidence>().ToListAsync());
        Assert.Empty(await result.InboxItems.ToListAsync());
        Assert.Equal(8, await result.Set<LearningAppliedOperation>().CountAsync());
        Assert.All(await result.Set<LearningRequest>().ToListAsync(), request =>
        {
            Assert.Equal(LearningRequestStatus.Completed, request.Status);
            Assert.False(string.IsNullOrWhiteSpace(request.Outcome));
        });
        Assert.Equal(0, recovery.KindCalls.GetValueOrDefault("assess"));
    }

    private WebApplicationFactory<Program> ExperienceHost(Probe probe, ExperienceSender sender) => EnglishHost(probe).WithWebHostBuilder(builder =>
        builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(TimeProvider.System));
            services.Replace(ServiceDescriptor.Singleton<ITelegramMessageSender>(sender));
            services.AddHostedService<TelegramOutboxWorker>();
        }));

    private async Task WaitExperienceDeliveryAsync()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var db = Open();
            if (!await db.TelegramOutboxMessages.AnyAsync(message => message.Status != OwnDay.Infrastructure.Persistence.OutboxMessageStatus.Sent)) return;
            await Task.Delay(50);
        }
        Assert.Fail("Hosted outbox did not deliver all committed replies.");
    }

    private sealed class ExperienceSender : ITelegramMessageSender
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public Task SendMessageAsync(long chatId, string text, string? replyMarkupJson, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Enqueue(text);
            return Task.CompletedTask;
        }
    }
}
