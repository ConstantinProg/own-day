using System.Text.Json;
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

public sealed class PostgresAdaptiveTeachingTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Post_AdaptationAcrossHostRestarts_PreservesMigratedRouteEvidenceProgressAndOutbox()
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
            var scopedConnection = connection + ";Search Path=" + schema;
            var options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(scopedConnection).Options;
            const string previousMigration = "20261105191515_AddLearnerKnowledge";
            var original = LearningProgram.Create(new UserId(101), "algebra", "Beginner", null, "Mathematics",
                [new("basics", "Basics", []), new("advanced", "Advanced", ["basics"])], null);
            await using (var setup = new OwnDayDbContext(options))
            {
                await setup.GetService<IMigrator>().MigrateAsync(previousMigration);
                await setup.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO learning_programs ("Id", "UserId", "Goal", "Experience", "Discipline", "Priority", "DiagnosticPending")
                    VALUES ({original.Id}, 101, 'algebra', 'Beginner', 'Mathematics', 1, false)
                    """);
                var route = original.CurrentRoute;
                var topicsJson = JsonSerializer.Serialize(route.Topics);
                await setup.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO learning_routes ("Id", "ProgramId", "Version", "Reason", "Topics")
                    VALUES ({route.Id}, {original.Id}, 1, {route.Reason}, CAST({topicsJson} AS jsonb))
                    """);
                await setup.Database.MigrateAsync();
                Assert.Null((await setup.Set<CurriculumRoute>().SingleAsync()).ProgressVersion);
            }
            var planner = new TelegramAdaptiveTeachingTests.Planner();
            var teacher = new TelegramAdaptiveTeachingTests.Teacher();
            Guid? skillId = null;
            for (var restart = 0; restart < 3; restart++)
            {
                using var factory = new OwnDayHostFactory();
                using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<OwnDayDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<OwnDayDbContext>>();
                    services.AddDbContext<OwnDayDbContext>(builder => builder.UseNpgsql(scopedConnection));
                    TelegramAdaptiveTeachingTests.Configure(services, planner, teacher);
                }));
                using var client = host.CreateClient();
                if (restart == 0)
                {
                    await Post(client, 1, "/learn_next");
                    await Post(client, 2, "/answer wrong");
                    await Post(client, 3, "/learn_next");
                    await Post(client, 4, "/answer wrong again");
                    await Post(client, 5, "/learn_next");
                }
                else if (restart == 1)
                {
                    await Post(client, 5, "/learn_next");
                    await Post(client, 6, "/learn_pause");
                    await Post(client, 7, "/learn_resume");
                    await Post(client, 8, "/answer correct");
                }
                else
                {
                    await Post(client, 9, "/learn_next");
                    await Post(client, 9, "/learn_next");
                    await TelegramCurriculumTests.PostAsync(client, 10, 202, "/learn_adapt");
                }
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
                var program = await db.Set<LearningProgram>().Include(p => p.Routes).SingleAsync();
                Assert.Equal(original.Id, program.Id);
                Assert.Equal("algebra", program.Goal);
                Assert.Equal(2, program.Routes.Count);
                Assert.Equal(1, program.CurrentRoute.ProgressVersion);
                Assert.Contains("2 negative", program.CurrentRoute.Reason);
                var basics = await db.Set<Lesson>().Where(l => l.TopicCode == "basics").ToListAsync();
                var currentSkillId = Assert.Single(basics.Select(l => l.SkillId).Distinct());
                skillId ??= currentSkillId;
                Assert.Equal(skillId, currentSkillId);
                Assert.Equal(3, basics.Count);
                Assert.Equal(restart == 0 ? 2 : 3, await db.Set<LearningEvidence>().CountAsync());
                Assert.Single(await db.TelegramOutboxMessages.ToListAsync(), m => m.Text.StartsWith("Teaching adjustment", StringComparison.Ordinal));
                if (restart == 2)
                {
                    var active = await db.Set<Lesson>().SingleAsync(l => l.Status == LessonStatus.AwaitingAnswer);
                    Assert.Equal("advanced", active.TopicCode);
                    Assert.Equal(2, active.RouteVersion);
                    Assert.Equal(10, await db.ProcessedTelegramUpdates.CountAsync());
                }
            }
            Assert.Single(planner.Requests);
            Assert.Equal(4, teacher.Requests.Count);
            await using var rollback = new OwnDayDbContext(options);
            await rollback.GetService<IMigrator>().MigrateAsync(previousMigration);
            Assert.Equal(2, await rollback.Database.SqlQueryRaw<int>("""SELECT count(*)::integer AS "Value" FROM learning_routes""").SingleAsync());
            Assert.Equal(3, await rollback.Set<LearningEvidence>().CountAsync());
            Assert.Equal(4, await rollback.Set<Lesson>().CountAsync());
            await rollback.Database.MigrateAsync();
            Assert.All(await rollback.Set<CurriculumRoute>().ToListAsync(), r => Assert.Null(r.ProgressVersion));
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    private static Task Post(HttpClient client, int id, string text) => TelegramCurriculumTests.PostAsync(client, id, 101, text);
}
