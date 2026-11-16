using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class PostgresReviewTests
{
    [Theory]
    [InlineData(3, 8)]
    [InlineData(11, 1)]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Process_ReviewsAcrossMigrationAndRestarts_PreservesDeadlinesEvidenceAndPendingDelivery(int month, int day)
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
            const string previous = "20261106114130_AddAdaptiveTeaching";
            var scopedConnection = connection + ";Search Path=" + schema + ";Timezone=America/New_York";
            var options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(scopedConnection).Options;
            var clock = new TelegramReviewFixture.Clock { Now = new DateTimeOffset(2026, month, day, 12, 0, 0, TimeSpan.Zero) };
            var teacher = new TelegramReviewFixture.Teacher();
            var skill = Skill.Create(new UserId(101), "Fractions", "Apply fractions in a concrete setting");
            var lessonId = Guid.NewGuid();
            var observed = clock.Now.AddDays(-1);
            await using (var setup = new OwnDayDbContext(options))
            {
                await setup.GetService<IMigrator>().MigrateAsync(previous);
                await setup.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO learning_skills ("Id", "UserId", "Title", "Description")
                    VALUES ({skill.Id}, 101, {skill.Title}, {skill.Description})
                    """);
                await setup.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO learning_lessons ("Id", "UserId", "Topic", "Theory", "Question", "Answer", "Feedback", "IsCorrect", "Status", "SkillId")
                    VALUES ({lessonId}, 101, 'Fractions', 'Theory', 'Question', 'Answer', 'AI basis', true, 2, {skill.Id})
                    """);
                await setup.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO learning_evidence ("LessonId", "SkillId", "UserId", "ObservedAt", "Answer", "AssessmentFeedback", "AssessedCorrect", "AssessmentSource", "Method")
                    VALUES ({lessonId}, {skill.Id}, 101, {observed}, 'Answer', 'AI basis', true, 'legacy-model', 'AI answer assessment')
                    """);
                await setup.Database.MigrateAsync();
                var schedule = await setup.Set<ReviewSchedule>().SingleAsync();
                Assert.Equal(clock.Now, schedule.DueAt);
                Assert.Equal(0, schedule.SuccessfulReviews);
                Assert.False((await setup.Set<LearningEvidence>().SingleAsync()).IsReview);
            }

            for (var restart = 0; restart < 3; restart++)
            {
                if (restart == 2) clock.Now = clock.Now.AddDays(3);
                using var factory = new OwnDayHostFactory();
                using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<OwnDayDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<OwnDayDbContext>>();
                    services.AddDbContext<OwnDayDbContext>(builder => builder.UseNpgsql(scopedConnection));
                    TelegramReviewFixture.Configure(services, clock, teacher);
                }));
                using var client = host.CreateClient();
                if (restart == 0)
                {
                    await Post(host.Services, 1, "/learn_reviews");
                    await Post(host.Services, 2, $"/learn_review {skill.Id}");
                    await Post(host.Services, 3, "/learn_pause");
                }
                else if (restart == 1)
                {
                    await Post(host.Services, 2, $"/learn_review {skill.Id}");
                    await Post(host.Services, 4, "/learn_resume");
                    await Post(host.Services, 5, "/answer correct");
                    await Post(host.Services, 5, "/answer correct");
                }
                else
                {
                    await Post(host.Services, 6, $"/learn_review {skill.Id}");
                    await Post(host.Services, 7, "/answer correct");
                    await Post(host.Services, 8, $"/learn_status {skill.Id}");
                }
                using var inspection = host.Services.CreateScope();
                var db = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
                var schedule = await db.Set<ReviewSchedule>().SingleAsync();
                Assert.Equal(restart, schedule.SuccessfulReviews);
                Assert.Equal(restart == 0 ? clock.Now : clock.Now.AddDays(restart == 1 ? 3 : 7), schedule.DueAt);
                var evidence = await db.Set<LearningEvidence>().ToListAsync();
                Assert.Equal(restart + 1, evidence.Count);
                Assert.Equal(restart, evidence.Count(e => e.IsReview));
                Assert.All(evidence, e => Assert.Equal(skill.Id, e.SkillId));
                if (restart == 0) Assert.Equal(LessonStatus.Paused, (await db.Set<Lesson>().SingleAsync(l => l.IsReview)).Status);
                if (restart == 2)
                {
                    Assert.Equal(SkillMastery.Developing, LearnerSkillState.Evaluate(evidence).Mastery);
                    Assert.Equal(AnswerAssistance.Unknown, evidence.Single(item => item.LessonId == lessonId).Assistance);
                    Assert.Equal(2, LearnerSkillState.Evaluate(evidence).PositiveAssessments);
                    Assert.Equal(8, await db.Set<OwnDay.Application.Learning.Requests.LearningRequest>().CountAsync());
                }
                Assert.All(await db.TelegramOutboxMessages.ToListAsync(), message => Assert.Null(message.SentAt));
            }
            Assert.Equal(2, teacher.Requests.Count);
            Assert.All(teacher.Requests, r => Assert.True(r.IsReview));
            Assert.Equal(2, teacher.Assessed);
            await using var rollback = new OwnDayDbContext(options);
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => rollback.GetService<IMigrator>().MigrateAsync(previous));
            Assert.Equal(3, await rollback.Set<Lesson>().CountAsync());
            Assert.Equal(3, await rollback.Set<LearningEvidence>().CountAsync());
            await rollback.Database.MigrateAsync();
            var restored = await rollback.Set<ReviewSchedule>().SingleAsync();
            Assert.Equal(clock.Now.AddDays(7), restored.DueAt);
            Assert.Equal(2, restored.SuccessfulReviews);
            Assert.Equal(2, await rollback.Set<LearningEvidence>().CountAsync(e => e.IsReview));
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    private static Task Post(IServiceProvider services, int id, string text) => LearningWorkerFixture.ProcessAsync(services, id, 101, text);
}
