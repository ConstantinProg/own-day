using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LessonSuspensionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuspendRestore_CurrentQuestion_PreservesOriginalStateAndHistory(bool paused)
    {
        var program = Program(new(101));
        var lesson = Lesson.Start(program.UserId, "Topic", "Theory", "Question");
        lesson.LinkToProgram(program, "root");
        lesson.AddExplanation(LessonInteractionKind.Clarification, "Why?", "Help");
        if (paused) lesson.Pause();
        var original = lesson.Status;
        lesson.SuspendForProgramSwitch(program);
        Assert.Equal(LessonStatus.Suspended, lesson.Status);
        Assert.Equal(original, lesson.SuspendedFrom);
        Assert.Throws<InvalidOperationException>(() => lesson.Complete("Answer", "Feedback", true));
        Assert.Throws<InvalidOperationException>(() => lesson.Resume());
        Assert.Throws<InvalidOperationException>(() => lesson.Finish());
        Assert.Throws<InvalidOperationException>(() => lesson.RestoreForProgram(Program(program.UserId)));
        Assert.Throws<InvalidOperationException>(() => lesson.RestoreForProgram(Program(new(202))));
        lesson.RestoreForProgram(program);
        Assert.Equal(original, lesson.Status);
        Assert.Null(lesson.SuspendedForProgramId);
        Assert.Null(lesson.SuspendedFrom);
        Assert.Equal(program.Id, lesson.ProgramId);
        Assert.Equal(1, lesson.RouteVersion);
        Assert.Equal("Question", lesson.Question);
        Assert.Equal("Help", Assert.Single(lesson.Interactions).Explanation);
        Assert.Equal(AnswerAssistance.Assisted, lesson.Assistance);
        Assert.Null(lesson.Answer);
    }

    [Fact]
    public void Suspend_ForeignProgramOrEndedLesson_RejectsWithoutMutation()
    {
        var program = Program(new(101));
        var lesson = Lesson.Start(program.UserId, "Topic", "Theory", "Question");
        Assert.Throws<InvalidOperationException>(() => lesson.SuspendForProgramSwitch(Program(new(202))));
        lesson.LinkToProgram(program, "root");
        Assert.Throws<InvalidOperationException>(() => lesson.SuspendForProgramSwitch(Program(program.UserId)));
        lesson.Complete("Answer", "Feedback", true);
        Assert.Throws<InvalidOperationException>(() => lesson.SuspendForProgramSwitch(program));
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.Null(lesson.SuspendedForProgramId);
    }

    private static LearningProgram Program(UserId user) => LearningProgram.Create(user, "Goal", "Experience", null,
        "English", [new("root", "Topic", [])], null);
}
