using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Actions;
using OwnDay.Domain.Learning;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class PostgresProjectLearningTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Post_ProjectPracticeAcrossMigrationAndRestarts_PreservesTaskLinkWithoutCompletingRealWork()
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
            const string previous = "20261106172103_AddLessonVerification";
            var scopedConnection = connection + ";Search Path=" + schema;
            var options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(scopedConnection).Options;
            await using (var setup = new OwnDayDbContext(options))
            {
                await setup.GetService<IMigrator>().MigrateAsync(previous);
                await setup.Database.ExecuteSqlRawAsync("""
                    INSERT INTO learning_lessons ("Id", "UserId", "Topic", "Theory", "Question", "Status", "IsReview")
                    VALUES ('11111111-1111-1111-1111-111111111111', 303, 'Legacy', 'Theory', 'Question', 0, false)
                    """);
                await setup.Database.MigrateAsync();
                Assert.Null((await setup.Set<Lesson>().SingleAsync()).ProjectActionId);
            }
            long projectId = 0, actionId = 0, foreignActionId = 0;
            var teacher = new TelegramCurriculumTests.Teacher();
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
                    (projectId, actionId) = await TelegramProjectLearningTests.SeedAsync(host.Services, 101, "Website", "Calculate page size");
                    (_, foreignActionId) = await TelegramProjectLearningTests.SeedAsync(host.Services, 202, "Foreign", "Private task");
                    await Post(client, 1, $"/learn_project {projectId} | Beginner");
                    await Post(client, 2, $"/learn_project_practice basics | {actionId}");
                    await Post(client, 3, "/learn_pause");
                }
                else if (restart == 1)
                {
                    await Post(client, 2, $"/learn_project_practice basics | {actionId}");
                    await Post(client, 4, "/learn_resume");
                    await Post(client, 5, "/answer correct");
                    await Post(client, 5, "/answer correct");
                }
                else
                {
                    await Post(client, 6, "/learn_project_report");
                    await Post(client, 6, "/learn_project_report");
                }
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
                var lesson = await db.Set<Lesson>().SingleAsync(l => l.UserId == new UserId(101));
                Assert.Equal(actionId, lesson.ProjectActionId);
                Assert.Equal(restart == 0 ? LessonStatus.Paused : LessonStatus.Completed, lesson.Status);
                Assert.Equal(ActionStatus.Active, (await db.Actions.SingleAsync(a => a.Id == actionId)).Status);
                Assert.Equal(ProjectStatus.Active, (await db.Projects.SingleAsync(p => p.Id == projectId)).Status);
                Assert.Equal(restart == 0 ? 0 : 1, await db.Set<LearningEvidence>().CountAsync());
                var replies = await db.TelegramOutboxMessages.ToListAsync();
                Assert.All(replies, r => Assert.Equal(OutboxMessageStatus.Pending, r.Status));
                Assert.Contains(replies, r => r.Text.Contains($"task #{actionId}", StringComparison.Ordinal));
                if (restart == 2)
                {
                    Assert.Equal(6, await db.ProcessedTelegramUpdates.CountAsync());
                    Assert.Contains(replies, r => r.Text.Contains("1 positive, 0 negative", StringComparison.Ordinal));
                }
            }
            Assert.Single(teacher.Requests);
            await using (var foreignLink = new OwnDayDbContext(options))
            {
                var program = await foreignLink.Set<LearningProgram>().Include(p => p.Routes).SingleAsync();
                var forged = Lesson.Start(new(101), "Forged", "Theory", "Question");
                forged.LinkToProgram(program, "basics");
                var foreignAction = await foreignLink.Actions.SingleAsync(a => a.Id == foreignActionId);
                Assert.Throws<ArgumentException>(() => forged.LinkToProjectAction(program, foreignAction));
                foreignLink.Entry(forged).Property(l => l.ProjectActionId).CurrentValue = foreignActionId;
                foreignLink.Add(forged);
                await Assert.ThrowsAsync<DbUpdateException>(() => foreignLink.SaveChangesAsync());
            }
            await using (var rollback = new OwnDayDbContext(options))
            {
                await rollback.GetService<IMigrator>().MigrateAsync(previous);
                await rollback.Database.MigrateAsync();
                var lesson = await rollback.Set<Lesson>().SingleAsync(l => l.UserId == new UserId(101));
                Assert.Null(lesson.ProjectActionId);
                Assert.Equal("correct", lesson.Answer);
                Assert.Single(await rollback.Set<LearningEvidence>().ToListAsync());
                Assert.Equal(ActionStatus.Active, (await rollback.Actions.SingleAsync(a => a.Id == actionId)).Status);
            }
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    private static Task Post(HttpClient client, int id, string command) => TelegramCurriculumTests.PostAsync(client, id, 101, command);
}
