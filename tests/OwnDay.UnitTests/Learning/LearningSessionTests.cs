using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LearningSessionTests
{
    [Fact]
    public void End_AtTwoHourInactivity_ExcludesLongGapsAndKeepsTheSavedQuestion()
    {
        var user = new UserId(6);
        var program = LearningProgram.Create(user, "Learn optics", "Beginner", null, "Physics", [new("light", "Light", [])], null);
        var lesson = Lesson.Start(user, "Light", "Theory", "What bends at the boundary?");
        lesson.LinkToProgram(program, "light");
        var start = DateTimeOffset.UnixEpoch;
        var session = LearningSession.Start(user, program.Id, start, 1, lesson);
        session.RecordActivity(start.AddMinutes(3), lesson);
        var lastActivity = start.AddMinutes(30);
        session.RecordActivity(lastActivity, lesson);
        var limit = lastActivity + LearningSession.IdleLimit;

        Assert.False(session.IsIdle(limit.AddTicks(-1)));
        Assert.True(session.IsIdle(limit));
        Assert.Throws<InvalidOperationException>(() => session.RecordActivity(limit, lesson));
        session.End(limit.AddHours(4), LearningSessionEnd.Idle, lesson);

        Assert.Equal(limit, session.EndedAt);
        Assert.Equal(180, session.ActiveSeconds);
        Assert.Equal(lesson.Question, session.Summary!.Question);
        Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
    }
}
