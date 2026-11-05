using Microsoft.EntityFrameworkCore;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LessonTests
{
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
