using Microsoft.EntityFrameworkCore;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed class PostgresLearningTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Save_LessonAcrossContexts_PreservesQuestionAndResult()
    {
        var connection = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION")
            ?? throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required.");
        var options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connection).Options;
        var user = new UserId(Random.Shared.NextInt64(1, long.MaxValue));
        Guid id;
        await using (var db = new OwnDayDbContext(options))
        {
            await db.Database.MigrateAsync();
            var lesson = Lesson.Start(user, "Algebra", "Theory", "Question");
            id = lesson.Id;
            db.Add(lesson);
            await db.SaveChangesAsync();
        }
        await using (var db = new OwnDayDbContext(options))
        {
            var lesson = await db.Set<Lesson>().SingleAsync(l => l.Id == id);
            Assert.Equal("Question", lesson.Question);
            lesson.Complete("Answer", "Feedback", false);
            await db.SaveChangesAsync();
        }
        await using (var db = new OwnDayDbContext(options))
        {
            var lesson = await db.Set<Lesson>().SingleAsync(l => l.Id == id);
            Assert.Equal(user, lesson.UserId);
            Assert.Equal("Answer", lesson.Answer);
            Assert.Equal("Feedback", lesson.Feedback);
            Assert.False(lesson.IsCorrect);
            db.Remove(lesson);
            await db.SaveChangesAsync();
        }
    }
}
