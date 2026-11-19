using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_GoalUpdateConfirmedAcrossRestart_PreservesExerciseAndCanRestoreOriginalGoal()
    {
        const string goal = "Handle typed API responses";
        const string criterion = "Implement one handler for both variants";
        const string original = "Read typed API responses";
        const string originalCriterion = "Recognize the response types";
        var probe = new Probe("")
        {
            Curriculum = new CurriculumProposal("TypeScript", [new("handlers", "Response handlers", [])], null),
            Assessment = (_, _) => new("Accepted", true)
        };
        var program = LearningProgram.Create(_user, original, "Beginner", null, "TypeScript",
            [new("basics", "Unions", [])], null, originalCriterion);
        await using (var db = Open())
        {
            db.AddRange(program, LearningContext.Create(program));
            await db.SaveChangesAsync();
        }
        Guid originalLesson;
        string code;
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "/learn_next");
            await SendEnglishAsync(client, 2, $"обновить цель на {goal} | {criterion}");
            await using var db = Open();
            Assert.Equal(original, (await db.Set<LearningProgram>().SingleAsync()).Goal);
            Assert.Single(await db.Set<CurriculumRoute>().ToListAsync());
            var consent = (await db.Set<DialogueContext>().SingleAsync()).PendingConfirmation!;
            Assert.Equal(DialogueOperationKind.ApplyGoalUpdate, consent.Operation);
            var replies = await db.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync();
            Assert.Contains(replies, reply => reply.Contains(criterion, StringComparison.Ordinal));
            Assert.Contains(replies, reply => reply.Contains("Response handlers", StringComparison.Ordinal));
            code = consent.Code;
            originalLesson = (await db.Set<Lesson>().SingleAsync()).Id;
        }

        using var restarted = ConsultationHost(probe);
        using var resumed = restarted.CreateClient();
        await SendEnglishAsync(resumed, 3, "/learn_confirm " + code);
        await SendEnglishAsync(resumed, 4, "/learn_confirm " + code);
        await using (var db = Open())
        {
            var changed = await db.Set<LearningProgram>().Include(value => value.Routes).SingleAsync();
            Assert.Equal(program.Id, changed.Id);
            Assert.Equal(goal, changed.Goal);
            Assert.Equal(criterion, changed.GoalCriterion);
            Assert.Equal(2, changed.CurrentRoute.Version);
            Assert.Equal([2], changed.ProgressVersions(2));
            var exercise = await db.Set<Lesson>().SingleAsync();
            Assert.Equal(originalLesson, exercise.Id);
            Assert.Equal(1, exercise.RouteVersion);
        }
        Assert.Equal(1, probe.KindCalls["plan"]);
        await SendEnglishAsync(resumed, 5, "/answer Both types share a discriminant.");
        await SendEnglishAsync(resumed, 6, "откатить маршрут к версии 1");
        await using (var db = Open())
            code = (await db.Set<DialogueContext>().SingleAsync()).PendingConfirmation!.Code;
        await SendEnglishAsync(resumed, 7, "/learn_confirm " + code);

        await using var result = Open();
        var restored = await result.Set<LearningProgram>().Include(value => value.Routes).SingleAsync();
        Assert.Equal(original, restored.Goal);
        Assert.Equal(originalCriterion, restored.GoalCriterion);
        Assert.Equal(3, restored.CurrentRoute.Version);
        Assert.Equal(1, restored.CurrentRoute.Path!.RestoredFrom);
        Assert.Equal([1, 3], restored.ProgressVersions(3));
        Assert.Equal(originalLesson, (await result.Set<LearningEvidence>().SingleAsync()).LessonId);
    }
}
