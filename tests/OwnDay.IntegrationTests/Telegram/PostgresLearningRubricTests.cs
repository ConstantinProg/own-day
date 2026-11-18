using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
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
