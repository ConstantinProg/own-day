using Microsoft.EntityFrameworkCore;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_SessionEndsAndExpiresAcrossRestart_PreservesTheQuestionAndRejectsTheLateAnswer()
    {
        var probe = new Probe("");
        Guid lessonId;
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            string[] turns = ["Хочу изучить TypeScript", "Типизировать ответы API", "Начинаю с нуля", "Продолжим", "/learn_end_session"];
            for (var index = 0; index < turns.Length; index++) await SendEnglishAsync(client, index + 1, turns[index]);
            await using var db = Open();
            var lesson = await db.Set<Lesson>().SingleAsync();
            lessonId = lesson.Id;
            var session = await db.Set<LearningSession>().SingleAsync();
            Assert.Equal(LearningSessionEnd.Explicit, session.EndReason);
            Assert.Equal(lessonId, session.Summary!.LessonId);
            Assert.Equal(probe.Material.Question, session.Summary.Question);
        }

        using var restarted = ConsultationHost(probe);
        using var restartedClient = restarted.CreateClient();
        await SendEnglishAsync(restartedClient, 6, "Продолжим");
        await using var result = Open();
        var backdated = DateTimeOffset.UtcNow.AddHours(-3);
        var reopened = await result.Set<LearningSession>().AsNoTracking().SingleAsync(session => session.EndedAt == null);
        var previous = backdated.AddHours(-1);
        await result.Set<LearningSession>().Where(session => session.Id != reopened.Id).ExecuteUpdateAsync(setters =>
            setters.SetProperty(session => session.StartedAt, previous).SetProperty(session => session.LastActivityAt, previous)
                .SetProperty(session => session.EndedAt, previous));
        await result.Set<LearningSession>().Where(session => session.Id == reopened.Id).ExecuteUpdateAsync(setters =>
            setters.SetProperty(session => session.StartedAt, backdated).SetProperty(session => session.LastActivityAt, backdated));
        await SendEnglishAsync(restartedClient, 7, "/answer stale answer");

        var closed = await result.Set<LearningSession>().AsNoTracking().SingleAsync(session => session.Id == reopened.Id);
        Assert.Equal(LearningSessionEnd.Idle, closed.EndReason);
        Assert.Equal(backdated.AddHours(2).ToUnixTimeSeconds(), closed.EndedAt!.Value.ToUnixTimeSeconds());
        Assert.Equal(0, closed.ActiveSeconds);
        var lessonAfter = await result.Set<Lesson>().AsNoTracking().SingleAsync();
        Assert.Equal(lessonId, lessonAfter.Id);
        Assert.Equal(LessonStatus.AwaitingAnswer, lessonAfter.Status);
        Assert.Empty(probe.AssessedAnswers);
        Assert.Equal(1, probe.KindCalls["generate"]);

        await SendEnglishAsync(restartedClient, 8, "/answer new answer");
        Assert.Equal("new answer", Assert.Single(probe.AssessedAnswers));
        Assert.False(result.Database.HasPendingModelChanges());
    }
}
