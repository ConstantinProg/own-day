using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class PostgresProgramsTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Post_ProgramsAcrossUpgradeAndRestarts_PreservesSelectionSharedIdentityAndAtomicReplies()
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
            const string previous = "20261106122410_AddSpacedReviews";
            var scopedConnection = connection + ";Search Path=" + schema;
            var options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(scopedConnection).Options;
            var source = LearningProgram.Create(new UserId(101), "Algebra", "Beginner", null, "Mathematics",
                [new("basics", "Basics", []), new("advanced", "Advanced", ["basics"])], null);
            var skill = Skill.Create(source.UserId, "Basics", "Algebraic concept in mathematics");
            var initial = Lesson.Start(source.UserId, "Basics", "Theory", "Question");
            initial.LinkToProgram(source, "basics");
            initial.LinkToSkill(skill);
            initial.Complete("Answer", "AI feedback", true);
            var evidence = LearningEvidence.FromAnswer(initial, DateTimeOffset.UnixEpoch, "test-model");
            await using (var setup = new OwnDayDbContext(options))
            {
                await setup.GetService<IMigrator>().MigrateAsync(previous);
                setup.AddRange(source, skill);
                await setup.SaveChangesAsync();
                // Seed historical columns explicitly; the current model includes newer verification metadata.
                await setup.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO learning_lessons ("Id", "UserId", "Topic", "Theory", "Question", "Answer", "Feedback", "IsCorrect", "Status", "ProgramId", "RouteVersion", "TopicCode", "SkillId", "IsReview")
                    VALUES ({initial.Id}, {source.UserId.Value}, {initial.Topic}, {initial.Theory}, {initial.Question}, {initial.Answer}, {initial.Feedback}, true, 2, {source.Id}, {initial.RouteVersion}, {initial.TopicCode}, {skill.Id}, false);
                    INSERT INTO learning_evidence ("LessonId", "SkillId", "UserId", "ObservedAt", "Answer", "AssessmentFeedback", "AssessedCorrect", "AssessmentSource", "Method", "IsReview")
                    VALUES ({initial.Id}, {skill.Id}, {source.UserId.Value}, {evidence.ObservedAt}, {evidence.Answer}, {evidence.AssessmentFeedback}, true, {evidence.AssessmentSource}, {evidence.Method}, false);
                    """);
                setup.Add(ReviewSchedule.FromAnswer(evidence));
                await setup.SaveChangesAsync();
                await setup.Database.MigrateAsync();
                Assert.Equal(source.Id, (await setup.Set<LearningContext>().SingleAsync()).ProgramId);
                Assert.Empty(await setup.Set<ProgramSkillLink>().ToListAsync());
                await setup.GetService<IMigrator>().MigrateAsync(previous);
                await setup.Database.MigrateAsync();
                Assert.Equal(source.Id, (await setup.Set<LearningContext>().AsNoTracking().SingleAsync()).ProgramId);
                Assert.Single(await setup.Set<LearningEvidence>().ToListAsync());
            }
            var planner = new TelegramCurriculumTests.Planner { NoQuestion = true, DifferentDiscipline = true };
            var teacher = new TelegramCurriculumTests.Teacher();
            for (var restart = 0; restart < 2; restart++)
            {
                using var factory = new OwnDayHostFactory();
                using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<OwnDayDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<OwnDayDbContext>>();
                    services.AddDbContext<OwnDayDbContext>(builder => builder.UseNpgsql(scopedConnection));
                    TelegramProgramsTests.Configure(services, planner, teacher);
                }));
                using var client = host.CreateClient();
                if (restart == 0)
                {
                    await Post(client, 1, "/learn_route");
                    await Post(client, 2, "/learn_program Physics | Beginner");
                    await Post(client, 3, $"/learn_link basics | {skill.Id} | The same concept in mechanics");
                    await Post(client, 4, "/learn_next");
                    await Post(client, 5, "/learn_pause");
                }
                else
                {
                    await Post(client, 3, $"/learn_link basics | {skill.Id} | Duplicate update");
                    await Post(client, 6, "/learn_links");
                    await Post(client, 7, "/learn_resume");
                    await Post(client, 8, "/answer correct");
                    await Post(client, 9, "/learn_focus basics");
                    await Post(client, 10, "/answer correct");
                    await Post(client, 11, $"/learn_select {source.Id}");
                    await Post(client, 12, "/learn_next");
                }
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
                var programs = await db.Set<LearningProgram>().Include(p => p.Routes).ToListAsync();
                Assert.Equal(2, programs.Count);
                var target = programs.Single(p => p.Id != source.Id);
                var link = Assert.Single(await db.Set<ProgramSkillLink>().ToListAsync());
                Assert.Equal(target.Id, link.ProgramId);
                Assert.Equal("The same concept in mechanics", link.Reason);
                Assert.Equal(skill.Id, link.SkillId);
                var context = await db.Set<LearningContext>().SingleAsync();
                Assert.Equal(restart == 0 ? target.Id : source.Id, context.ProgramId);
                var targetLessons = await db.Set<Lesson>().Where(l => l.ProgramId == target.Id).ToListAsync();
                Assert.All(targetLessons, l => Assert.Equal(skill.Id, l.SkillId));
                Assert.Equal(restart == 0 ? 1 : 2, targetLessons.Count);
                Assert.Equal(restart == 0 ? LessonStatus.Paused : LessonStatus.Completed, targetLessons[0].Status);
                var observations = await db.Set<LearningEvidence>().ToListAsync();
                Assert.Equal(restart == 0 ? 1 : 3, observations.Count);
                Assert.All(observations, e => Assert.Equal(skill.Id, e.SkillId));
                Assert.Single(await db.Set<ReviewSchedule>().ToListAsync());
                Assert.Single(programs.Single(p => p.Id == source.Id).Routes);
                Assert.All(await db.TelegramOutboxMessages.ToListAsync(), m => Assert.Null(m.SentAt));
                if (restart == 1)
                {
                    Assert.Equal(SkillMastery.Demonstrated, LearnerSkillState.Evaluate(observations).Mastery);
                    Assert.Equal(2, target.Routes.Count);
                    Assert.Equal(1, target.CurrentRoute.ProgressVersion);
                    Assert.Equal(12, await db.ProcessedTelegramUpdates.CountAsync());
                    Assert.Equal("Advanced", teacher.Requests[^1].Topic);
                }
            }
            Assert.Single(planner.Requests);
            Assert.Equal(3, teacher.Generated);
            Assert.All(teacher.Requests.Take(2), request => Assert.NotNull(request.SharedSkill));
            await using var constraints = new OwnDayDbContext(options);
            var foreign = Skill.Create(new UserId(202), "Foreign");
            constraints.Add(foreign);
            await constraints.SaveChangesAsync();
            var ownerFailure = await Assert.ThrowsAsync<PostgresException>(() => constraints.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE learning_contexts SET "UserId" = 202 WHERE "UserId" = 101
                """));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ownerFailure.SqlState);
            var skillFailure = await Assert.ThrowsAsync<PostgresException>(() => constraints.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE learning_program_skill_links SET "SkillId" = {foreign.Id}
                """));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, skillFailure.SqlState);
            // Restoring the old unique owner index must fail atomically rather than deleting programs.
            var rollbackFailure = await Assert.ThrowsAsync<PostgresException>(() => constraints.GetService<IMigrator>().MigrateAsync(previous));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, rollbackFailure.SqlState);
            Assert.Equal(2, await constraints.Set<LearningProgram>().CountAsync());
            Assert.Single(await constraints.Set<LearningContext>().ToListAsync());
            Assert.Equal(skill.Id, (await constraints.Set<ProgramSkillLink>().SingleAsync()).SkillId);
            Assert.Equal(3, await constraints.Set<LearningEvidence>().CountAsync());
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived isolated schema name.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    private static Task Post(HttpClient client, int id, string text) => TelegramCurriculumTests.PostAsync(client, id, 101, text);
}
