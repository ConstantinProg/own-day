using Microsoft.EntityFrameworkCore;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LessonTests
{
    [Fact]
    public void Pause_ResumeAndComplete_PreservesCurrentStep()
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        var step = (lesson.Topic, lesson.Theory, lesson.Question);
        lesson.AddExplanation(LessonInteractionKind.Clarification, "Detour", "Explanation");
        lesson.AddExplanation(LessonInteractionKind.AlternativeExplanation, "Explain differently", "Example");
        Assert.Equal(step, (lesson.Topic, lesson.Theory, lesson.Question));
        Assert.Equal(2, lesson.Interactions.Count);
        lesson.Pause();
        Assert.Equal(LessonStatus.Paused, lesson.Status);
        lesson.Resume();
        Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
        Assert.Equal(step, (lesson.Topic, lesson.Theory, lesson.Question));
        lesson.Complete("Answer", "Feedback", true);
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.Equal(step, (lesson.Topic, lesson.Theory, lesson.Question));
    }

    [Fact]
    public void Interactions_CompletedLesson_RejectsExternalMutation()
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        var history = lesson.Interactions;
        lesson.AddExplanation(LessonInteractionKind.Clarification, "Request", "Explanation");
        lesson.Complete("Answer", "Feedback", true);

        var interaction = Assert.Single(history);
        var collection = Assert.IsAssignableFrom<ICollection<LessonInteraction>>(history);
        Assert.Throws<NotSupportedException>(() => collection.Add(interaction));
        Assert.Throws<NotSupportedException>(() => collection.Clear());
        Assert.Same(interaction, Assert.Single(lesson.Interactions));
        Assert.Equal(LessonStatus.Completed, lesson.Status);
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
        var state = (lesson.Topic, lesson.Theory, lesson.Question, lesson.Answer, lesson.Feedback, lesson.IsCorrect);
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
        Assert.Equal(state, (lesson.Topic, lesson.Theory, lesson.Question, lesson.Answer, lesson.Feedback, lesson.IsCorrect));
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
    public void AddExplanation_InvalidOutput_PreservesStepAndHistory(string explanation)
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        var step = (lesson.Topic, lesson.Theory, lesson.Question);
        Assert.Throws<ArgumentException>(() => lesson.AddExplanation(LessonInteractionKind.Clarification, "Request", explanation));
        Assert.Empty(lesson.Interactions);
        Assert.Equal(step, (lesson.Topic, lesson.Theory, lesson.Question));
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
