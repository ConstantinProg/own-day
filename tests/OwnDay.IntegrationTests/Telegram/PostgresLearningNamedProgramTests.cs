using Microsoft.EntityFrameworkCore;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_NamedProgramSwitchWithAmbiguityAndNewRoute_RestoresOnlyTheOwnedQuestion()
    {
        var typescript = LearningProgram.Create(_user, "Types for API responses", "Beginner", null, "TypeScript",
            [new("basics", "Unions", []), new("next", "Types for handlers", ["basics"])], null);
        var finance = LearningProgram.Create(_user, "Финансы и equities", "Beginner", null, "Stock market", [new("risk", "Risk", [])], null);
        var another = LearningProgram.Create(_user, "Личные финансы", "Beginner", null, "Finance", [new("funds", "Funds", [])], null);
        var foreign = LearningProgram.Create(new UserId(202), "Чужие финансы", "Beginner", null, "Finance", [new("funds", "Funds", [])], null);
        await using (var db = Open())
        {
            db.AddRange(typescript, finance, another, foreign, LearningContext.Create(typescript));
            await db.SaveChangesAsync();
        }
        var probe = new Probe("")
        {
            Material = new("Unions share a discriminant", "What can you infer from the kind field?")
            {
                Content = new("unions", "rubric-v1", LearningContentKind.StructuredExercise, "Read discriminated unions",
                    "Explain the discriminant", "Explain which variant the check excludes.",
                    new(1, ExerciseEvidenceKind.Understanding, [new("kind", "Identify the excluded variant.")]))
            }
        };
        Guid originalLesson;
        Guid otherLesson;
        string returnCode;
        using (var host = SwitchHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "практика 2");
            await SendEnglishAsync(client, 2, "переключись на финансы");
            await using (var db = Open())
            {
                var replies = await db.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync();
                Assert.Contains(replies, reply => reply.Contains(finance.Id.ToString(), StringComparison.Ordinal));
                Assert.Contains(replies, reply => reply.Contains(another.Id.ToString(), StringComparison.Ordinal));
                Assert.DoesNotContain(replies, reply => reply.Contains(foreign.Id.ToString(), StringComparison.Ordinal));
                Assert.Null((await db.Set<DialogueContext>().SingleAsync()).PendingConfirmation);
                var exercise = await db.Set<Lesson>().SingleAsync();
                originalLesson = exercise.Id;
                Assert.Equal(originalLesson, Assert.Single((await db.Set<LearningContext>().SingleAsync()).Practice!.LessonIds));
                exercise.AddExplanation(LessonInteractionKind.Clarification, "What is kind?", "The clue is a common field.");
                await db.SaveChangesAsync();
            }
            await SendEnglishAsync(client, 3, "switch to equities");
            await SendEnglishAsync(client, 4, "/learn_confirm " + await SwitchCodeAsync());
            await SendEnglishAsync(client, 5, "/learn_next");
            await SendEnglishAsync(client, 6, "/learn_pause");
            await using (var db = Open())
            {
                otherLesson = (await db.Set<Lesson>().SingleAsync(lesson => lesson.ProgramId == finance.Id)).Id;
                var updated = await db.Set<LearningProgram>().Include(value => value.Routes).SingleAsync(value => value.Id == typescript.Id);
                Assert.True(updated.SetTopicSkipped(1, "basics", true, "Skip foundations"));
                await db.SaveChangesAsync();
            }
            await SendEnglishAsync(client, 7, "выбрать программу TypeScript");
            returnCode = await SwitchCodeAsync();
        }

        using var restarted = SwitchHost(probe);
        using var resumed = restarted.CreateClient();
        await SendEnglishAsync(resumed, 8, "/learn_confirm " + returnCode);

        await using var result = Open();
        Assert.Equal(typescript.Id, (await result.Set<LearningContext>().SingleAsync()).ProgramId);
        var restored = await result.Set<Lesson>().Include(lesson => lesson.Interactions).SingleAsync(lesson => lesson.Id == originalLesson);
        Assert.Equal(LessonStatus.AwaitingAnswer, restored.Status);
        Assert.Equal(1, restored.RouteVersion);
        Assert.Equal(probe.Material.Question, restored.Question);
        Assert.Equal("kind", Assert.Single(restored.Content!.StructuredRubric!.Criteria).Code);
        Assert.Equal(LessonInteractionKind.Clarification, Assert.Single(restored.Interactions).Kind);
        Assert.Equal(LessonStatus.Suspended, (await result.Set<Lesson>().SingleAsync(lesson => lesson.Id == otherLesson)).Status);
        Assert.Contains(await result.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync(),
            reply => reply.Contains("открыто для другой версии маршрута", StringComparison.Ordinal));
        Assert.Empty(await result.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(2, probe.KindCalls["generate"]);
    }
}
