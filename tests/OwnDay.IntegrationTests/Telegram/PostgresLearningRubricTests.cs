using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_CorrectionAndExplicitSolutionAcrossRestart_PreservesAttemptsAndMarksAssistedSuccess()
    {
        const string question = "Почему проверка kind сужает union?";
        const string guidance = "Рассмотрите два варианта с разными значениями kind.";
        const string first = "Ветка ничего не исключает.";
        const string success = "Проверка исключает варианты с другими значениями kind.";
        var probe = new Probe("")
        {
            Material = new("У общей метки может быть несколько значений.", question)
            {
                Content = new("unions", "rubric-v1", LearningContentKind.StructuredExercise,
                    "Понять сужение", "Описать механизм", "Связать проверку и исключение вариантов.",
                    new(1, ExerciseEvidenceKind.Understanding, [new("mechanism", "Укажите, какие варианты исключаются.")]))
            },
            Assessment = (_, answer) => new LessonAssessment("Обратная связь", answer == success)
            {
                Rubric = new([new("mechanism", answer == success ? RubricCriterionResult.Met : RubricCriterionResult.NotMet,
                    "Проверена связь с исключением вариантов.")]),
                Correction = new(ExerciseCorrectionKind.Example, guidance)
            }
        };

        Guid lessonId;
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            string[] turns = ["Хочу понять union types", "Разбирать обработчики TypeScript", "Начинаю с нуля", "Продолжим"];
            for (var index = 0; index < turns.Length; index++) await SendEnglishAsync(client, index + 1, turns[index]);
            await using var db = Open();
            lessonId = (await db.Set<Lesson>().SingleAsync()).Id;
        }
        using (var host = Host(probe, worker: false, publicIngress: true, interceptor: new RejectBoundary("apply"),
            configure: options => { options.Enabled = true; options.DialogueEnabled = true; }))
        using (var client = host.CreateClient())
        {
            using var accepted = await PostPublicAsync(client, 5, "/answer " + first);
            accepted.EnsureSuccessStatusCode();
            using var scope = host.Services.CreateScope();
            var lease = (await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        }
        await using (var db = Open())
        {
            Assert.Null((await db.Set<Lesson>().SingleAsync()).RubricEvaluation);
            await db.Database.ExecuteSqlRawAsync("UPDATE learning_requests SET lease_until = clock_timestamp() - interval '1 second' WHERE status = 2");
        }
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await WaitAllPublicAsync(5);
            await using var db = Open();
            var lesson = await db.Set<Lesson>().SingleAsync();
            Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
            Assert.Equal(first, Assert.Single(lesson.RubricEvaluation!.Corrections).Answer);
            Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        }

        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 6, "Продолжим");
            await SendEnglishAsync(client, 7, "/answer Варианты сохраняются все.");
            await SendEnglishAsync(client, 8, "дай подсказку");
            await SendEnglishAsync(client, 9, "покажи решение");
            await SendEnglishAsync(client, 10, "покажи решение");
            await SendEnglishAsync(client, 11, "/answer " + success);
            using var duplicate = await PostPublicAsync(client, 11, "/answer " + success);
            duplicate.EnsureSuccessStatusCode();
        }

        await using var result = Open();
        var completed = await result.Set<Lesson>().SingleAsync();
        var evidence = await result.Set<LearningEvidence>().SingleAsync();
        Assert.Equal(lessonId, completed.Id);
        Assert.Equal(question, completed.Question);
        Assert.Equal(2, completed.RubricEvaluation!.Corrections.Length);
        Assert.Equal(first, completed.RubricEvaluation.InitialAnswer);
        Assert.Equal(AnswerAssistance.Assisted, evidence.Assistance);
        Assert.True(evidence.AssessedCorrect);
        Assert.Equal(SkillMastery.Unknown, LearnerSkillState.Evaluate([evidence]).Mastery);
        Assert.Equal(LessonInteractionKind.FullAnswer, Assert.Single(probe.ExplanationKinds));
        Assert.Equal(3, probe.AssessedAnswers.Count);
        Assert.Contains(await result.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync(),
            text => text.Contains("/learn_practice", StringComparison.Ordinal));
        var rollback = await Assert.ThrowsAsync<PostgresException>(() =>
            result.GetService<IMigrator>().MigrateAsync("20261118160000_AddLearningSessions"));
        Assert.Contains("Retain correction and solution history", rollback.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_RubricFollowUpAcrossRestart_CreditsOnlyTheFinalAssessmentOnce()
    {
        const string requirement = "Опишите, почему изменяется тип значения.";
        const string originalAnswer = "Тип меняется.";
        const string clarification = "Проверка kind исключает остальные варианты объединения.";
        var content = new LearningContent("typescript", "rubric-v1", LearningContentKind.StructuredExercise,
            "Объяснить сужение типа", requirement, "Объяснение должно связывать проверку с исключением вариантов.",
            new(1, ExerciseEvidenceKind.Understanding, [new("mechanism", requirement)]));
        var probe = new Probe("")
        {
            Material = new("Проверка общего поля может сузить объединение.", "Что делает проверка kind?") { Content = content },
            Assessment = (lesson, _) => new LessonAssessment("Обратная связь", IsCorrect: lesson.RubricEvaluation is null)
            {
                Rubric = new([new("mechanism", lesson.RubricEvaluation is null
                    ? RubricCriterionResult.Unclear : RubricCriterionResult.Met, "Есть объяснение механизма.")])
            }
        };

        Guid originalPrompt;
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            string[] turns = ["Хочу изучить TypeScript", "Проверять типы в React", "Начинаю с нуля", "Продолжим"];
            for (var index = 0; index < turns.Length; index++) await SendEnglishAsync(client, index + 1, turns[index]);
            await using var db = Open();
            originalPrompt = (await db.Set<DialogueContext>().AsNoTracking().SingleAsync()).PromptId;
            Assert.Contains(await db.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync(),
                text => text.Contains(requirement, StringComparison.Ordinal));
            await SendEnglishAsync(client, 5, "/answer " + originalAnswer);
            var lesson = await db.Set<Lesson>().SingleAsync();
            Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
            Assert.Equal(requirement, lesson.ResponseQuestion);
            Assert.NotEqual(originalPrompt, (await db.Set<DialogueContext>().SingleAsync()).PromptId);
            Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        }

        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 6, "Продолжим");
            await SendEnglishAsync(client, 7, "/answer " + clarification);
            using var duplicate = await PostPublicAsync(client, 7, "/answer " + clarification);
            duplicate.EnsureSuccessStatusCode();
        }

        await using var result = Open();
        var evidence = await result.Set<LearningEvidence>().SingleAsync();
        var completed = await result.Set<Lesson>().SingleAsync();
        var operation = await result.Set<LearningRequest>().SingleAsync(request => request.SourceUpdateId == 7);
        Assert.True(evidence.AssessedCorrect);
        Assert.Equal(originalAnswer, evidence.RubricEvidence!.Evaluation.InitialAnswer);
        Assert.Equal(operation.Id, evidence.RubricEvidence.Evaluation.OperationId);
        Assert.Equal(clarification, completed.Answer);
        Assert.Equal(content.Version, evidence.Content!.Version);
        Assert.Equal(SkillMastery.Developing, LearnerSkillState.Evaluate([evidence]).Mastery);
        Assert.Equal(2, probe.AssessedAnswers.Count);
        Assert.False(result.Database.HasPendingModelChanges());
    }
}
