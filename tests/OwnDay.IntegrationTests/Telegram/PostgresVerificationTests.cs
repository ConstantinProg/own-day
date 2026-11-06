using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class PostgresVerificationTests
{
    [Theory]
    [InlineData(MaterialSourceStatus.QuoteMatched)]
    [InlineData(MaterialSourceStatus.Unavailable)]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Post_VerificationAcrossMigrationAndRestarts_PreservesUncertaintyAndPreventsUnsafeDowngrade(MaterialSourceStatus sourceStatus)
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
            const string previous = "20261106152635_AddStudySchedules";
            var scopedConnection = connection + ";Search Path=" + schema;
            var options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(scopedConnection).Options;
            await using (var setup = new OwnDayDbContext(options))
            {
                await setup.GetService<IMigrator>().MigrateAsync(previous);
                await setup.Database.ExecuteSqlRawAsync("""
                    INSERT INTO learning_skills ("Id", "UserId", "Title", "Description")
                    VALUES ('11111111-1111-1111-1111-111111111111', 303, 'Legacy skill', 'Description');
                    INSERT INTO learning_lessons ("Id", "UserId", "Topic", "Theory", "Question", "Answer", "Feedback", "IsCorrect", "Status", "IsReview", "SkillId")
                    VALUES ('22222222-2222-2222-2222-222222222222', 303, 'Legacy', 'Theory', 'Question', 'Answer', 'Feedback', true, 2, false, '11111111-1111-1111-1111-111111111111');
                    INSERT INTO learning_evidence ("LessonId", "SkillId", "UserId", "ObservedAt", "Answer", "AssessmentFeedback", "AssessedCorrect", "AssessmentSource", "Method", "IsReview")
                    VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 303, now(), 'Answer', 'Feedback', true, 'legacy-model', 'AI answer assessment', false);
                    """);
                await setup.Database.MigrateAsync();
                var legacy = await setup.Set<Lesson>().AsNoTracking().SingleAsync();
                Assert.Null(legacy.Provenance);
                Assert.Null(legacy.ExpectedAnswer);
                Assert.True(legacy.AiIsCorrect);
                Assert.True((await setup.Set<LearningEvidence>().AsNoTracking().SingleAsync()).AiAssessedCorrect);
                await setup.GetService<IMigrator>().MigrateAsync(previous);
                await setup.Database.MigrateAsync();
            }
            var provenance = new LessonProvenance("fake generator", "https://example.org/reference", "v1",
                sourceStatus == MaterialSourceStatus.Unavailable ? null : DateTimeOffset.UtcNow,
                sourceStatus == MaterialSourceStatus.Unavailable ? null : "Theory", sourceStatus);
            var teacher = new Teacher(provenance);
            for (var restart = 0; restart < 3; restart++)
            {
                using var factory = new OwnDayHostFactory();
                using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<OwnDayDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<OwnDayDbContext>>();
                    services.AddDbContext<OwnDayDbContext>(builder => builder.UseNpgsql(scopedConnection));
                    services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(new TelegramCurriculumTests.Planner { NoQuestion = true }));
                    services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
                }));
                using var client = host.CreateClient();
                if (restart == 0)
                {
                    await Post(client, 1, "/learn_program Mathematics | Beginner");
                    await Post(client, 2, "/learn_next");
                    await Post(client, 3, "/learn_pause");
                }
                else if (restart == 1)
                {
                    await Post(client, 2, "/learn_next");
                    await Post(client, 4, "/learn_resume");
                    await Post(client, 5, "/answer five");
                    await Post(client, 5, "/answer five");
                }
                else
                {
                    await Post(client, 6, "/learn_next");
                }
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
                var lessons = await db.Set<Lesson>().AsNoTracking().Where(l => l.UserId == new UserId(101)).ToListAsync();
                Assert.All(lessons, l =>
                {
                    Assert.Equal(provenance, l.Provenance);
                    Assert.Equal("four", l.ExpectedAnswer);
                    Assert.Equal("basics", l.TopicCode);
                });
                if (restart > 0)
                {
                    var evidence = Assert.Single(await db.Set<LearningEvidence>().AsNoTracking().Where(e => e.UserId == new UserId(101)).ToListAsync());
                    Assert.Equal(AnswerAssessmentStatus.Conflict, evidence.AssessmentStatus);
                    Assert.True(evidence.AiAssessedCorrect);
                    Assert.False(evidence.DeterministicIsCorrect);
                    Assert.False(evidence.AssessedCorrect);
                    Assert.Equal(SkillMastery.Conflicting, LearnerSkillState.Evaluate([evidence]).Mastery);
                    await Post(client, 20 + restart, "/learn_status " + evidence.SkillId);
                }
                var messages = await db.TelegramOutboxMessages.AsNoTracking().Where(m => m.ChatId == 101).ToListAsync();
                Assert.All(messages, m => Assert.Null(m.SentAt));
                Assert.Contains(messages, m => m.Text.Contains(sourceStatus == MaterialSourceStatus.Unavailable
                    ? "Source unavailable" : "Excerpt matched", StringComparison.Ordinal));
                if (restart > 0) Assert.Contains(messages, m => m.Text.Contains("Conflict:", StringComparison.Ordinal));
            }
            Assert.Equal(1, teacher.Assessed);
            await using var rollback = new OwnDayDbContext(options);
            var error = await Assert.ThrowsAsync<PostgresException>(() => rollback.GetService<IMigrator>().MigrateAsync(previous));
            Assert.Contains("conflicting assessments", error.MessageText);
            Assert.Equal(AnswerAssessmentStatus.Conflict, (await rollback.Set<LearningEvidence>().SingleAsync(e => e.UserId == new UserId(101))).AssessmentStatus);
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    private static Task Post(HttpClient client, int id, string text) => TelegramReviewTests.Post(client, id, 101, text);

    private sealed class Teacher(LessonProvenance provenance) : ILessonTeacher
    {
        public int Assessed { get; private set; }
        public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token) =>
            Task.FromResult(new LessonMaterial("Theory", "Two plus two?") { ExpectedAnswer = "four", Provenance = provenance });
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assessed++;
            return Task.FromResult(new LessonAssessment("AI says correct", true));
        }
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
