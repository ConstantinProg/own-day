using Microsoft.EntityFrameworkCore;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LessonTests
{
    [Fact]
    public void Pause_ResumeAndComplete_PreservesQuestionAndExpectedAction()
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        var context = lesson.ContextSummary;
        lesson.AddExplanation(LessonInteractionKind.Clarification, "Detour", "Explanation");
        lesson.AddExplanation(LessonInteractionKind.AlternativeExplanation, "Explain differently", "Example");
        Assert.Equal(context, lesson.ContextSummary);
        Assert.Equal(2, lesson.Interactions.Count);
        lesson.Pause();
        Assert.Equal(LessonStatus.Paused, lesson.Status);
        Assert.Contains("Resume the lesson", lesson.ContextSummary);
        lesson.Resume();
        Assert.Equal(context, lesson.ContextSummary);
        lesson.Complete("Answer", "Feedback", true);
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.Contains("Lesson ended", lesson.ContextSummary);
        Assert.Equal("Question", lesson.Question);
    }

    [Theory]
    [InlineData(LessonStatus.Paused, "pause")]
    [InlineData(LessonStatus.Paused, "answer")]
    [InlineData(LessonStatus.Paused, "explain")]
    [InlineData(LessonStatus.AwaitingAnswer, "resume")]
    [InlineData(LessonStatus.Completed, "pause")]
    [InlineData(LessonStatus.Completed, "resume")]
    [InlineData(LessonStatus.Completed, "finish")]
    [InlineData(LessonStatus.Completed, "answer")]
    [InlineData(LessonStatus.Completed, "explain")]
    [InlineData(LessonStatus.Finished, "pause")]
    [InlineData(LessonStatus.Finished, "resume")]
    [InlineData(LessonStatus.Finished, "finish")]
    [InlineData(LessonStatus.Finished, "answer")]
    [InlineData(LessonStatus.Finished, "explain")]
    public void Transition_InvalidState_RejectsWithoutMutation(LessonStatus status, string action)
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        switch (status)
        {
            case LessonStatus.Paused: lesson.Pause(); break;
            case LessonStatus.Completed: lesson.Complete("Answer", "Feedback", false); break;
            case LessonStatus.Finished: lesson.Finish(); break;
        }
        var context = lesson.ContextSummary;
        Assert.Throws<InvalidOperationException>(() =>
        {
            switch (action)
            {
                case "pause": lesson.Pause(); break;
                case "resume": lesson.Resume(); break;
                case "finish": lesson.Finish(); break;
                case "answer": lesson.Complete("Other", "Other feedback", true); break;
                case "explain": lesson.AddExplanation(LessonInteractionKind.Clarification, "Request", "Explanation"); break;
            }
        });
        Assert.Equal(status, lesson.Status);
        Assert.Equal(context, lesson.ContextSummary);
        Assert.Empty(lesson.Interactions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Finish_OpenLesson_EndsWithoutAssessment(bool paused)
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        if (paused) lesson.Pause();
        lesson.Finish();
        Assert.Equal(LessonStatus.Finished, lesson.Status);
        Assert.Null(lesson.Answer);
        Assert.Null(lesson.Feedback);
        Assert.Null(lesson.IsCorrect);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AddExplanation_InvalidOutput_PreservesContextAndHistory(string explanation)
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        var context = lesson.ContextSummary;
        Assert.Throws<ArgumentException>(() => lesson.AddExplanation(LessonInteractionKind.Clarification, "Request", explanation));
        Assert.Empty(lesson.Interactions);
        Assert.Equal(context, lesson.ContextSummary);
    }

    [Fact]
    public void Model_LearningMigration_MatchesSnapshot()
    {
        using var db = new OwnDay.Infrastructure.Persistence.OwnDayDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<OwnDay.Infrastructure.Persistence.OwnDayDbContext>()
                .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Complete_SecondAnswer_RejectsTransition()
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        lesson.Complete("Answer", "Feedback", false);
        Assert.False(lesson.IsCorrect);
        Assert.Throws<InvalidOperationException>(() => lesson.Complete("Other", "Feedback", true));
        Assert.Equal("Answer", lesson.Answer);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Start_InvalidMaterial_RejectsLesson(string theory)
    {
        Assert.Throws<ArgumentException>(() => Lesson.Start(new UserId(1), "Topic", theory, "Question"));
    }
}
