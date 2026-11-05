using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed class PostgresLearningTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Migrate_ExistingLessons_BackfillsStatesAndContextWithoutLosingResults()
    {
        var connection = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required.");
        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new OwnDayDbContext(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connection).Options);
#pragma warning disable EF1003 // Schema name is generated from a GUID.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003
        try
        {
            var options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connection + ";Search Path=" + schema).Options;
            await using var db = new OwnDayDbContext(options);
            // This existing migration uses a short timestamp; resolve its name with EF's ID generator.
            var legacyName = db.GetService<IMigrationsIdGenerator>().GetName("202611050001_AddLearningLessons");
            await db.GetService<IMigrator>().MigrateAsync(legacyName);
            var user = new UserId(101);
            foreach (var completed in new[] { true, true, false })
            {
                var id = Guid.NewGuid();
                string? answer = completed ? "Answer" : null;
                string? feedback = completed ? "Feedback" : null;
                bool? correct = completed ? true : null;
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO learning_lessons ("Id", "UserId", "Topic", "Theory", "Question", "Answer", "Feedback", "IsCorrect")
                    VALUES ({id}, {user.Value}, 'Algebra', 'Theory', 'Question', {answer}, {feedback}, {correct})
                    """);
            }
            await db.Database.MigrateAsync();
            var lessons = await db.Set<Lesson>().ToListAsync();
            Assert.Equal(2, lessons.Count(l => l.Status == LessonStatus.Completed && l.Answer == "Answer" && l.IsCorrect == true));
            var active = Assert.Single(lessons, l => l.Status == LessonStatus.AwaitingAnswer);
            Assert.Equal(Lesson.Start(user, "Algebra", "Theory", "Question").ContextSummary, active.ContextSummary);
            active.Finish();
            await db.SaveChangesAsync();
            db.Add(Lesson.Start(user, "Geometry", "Theory", "Next question"));
            await db.SaveChangesAsync();
            Assert.Equal(4, await db.Set<Lesson>().CountAsync());
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

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
