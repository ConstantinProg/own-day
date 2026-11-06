using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class PostgresPersonalizationTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Post_PersonalizationAcrossMigrationAndRestarts_PreservesPreferencesEvidenceAndTransferBasis()
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
            const string previous = "20261106175925_AddProjectLearningPractice";
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
                var legacy = await setup.Set<Lesson>().SingleAsync();
                Assert.Null(legacy.Teaching);
                Assert.Null(legacy.Transfer);
                Assert.Empty(await setup.Set<LearningPreference>().ToListAsync());
            }
            Guid source = default, target = default, skill = default;
            var teacher = new TelegramCurriculumTests.Teacher();
            for (var restart = 0; restart < 3; restart++)
            {
                using var factory = new OwnDayHostFactory();
                using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<OwnDayDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<OwnDayDbContext>>();
                    services.AddDbContext<OwnDayDbContext>(builder => builder.UseNpgsql(scopedConnection));
                    services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
                }));
                using var client = host.CreateClient();
                if (restart == 0)
                {
                    (source, target, skill) = await TelegramPersonalizationTests.SeedTransferAsync(host.Services);
                    await Post(client, 1, "/learn_preference format | dialogue");
                    await Post(client, 2, "/learn_preference depth | detailed");
                    await Post(client, 3, "/learn_preference pace | one step at a time");
                    await Post(client, 4, "/learn_preference examples | music");
                    await Post(client, 5, $"/learn_link basics | {skill} | Algebra applies to mechanics");
                    await Post(client, 6, $"/learn_transfer basics | {source} | basics");
                    await Post(client, 7, "/learn_pause");
                }
                else if (restart == 1)
                {
                    await Post(client, 6, $"/learn_transfer basics | {source} | basics");
                    await Post(client, 8, "/learn_resume");
                    await Post(client, 9, "/answer correct");
                    await Post(client, 9, "/answer correct");
                    await Post(client, 10, "/learn_preferences");
                }
                else
                {
                    await Post(client, 11, "/learn_preferences");
                    await Post(client, 12, "/learn_preference depth | brief");
                    await Post(client, 13, $"/learn_practice {skill}");
                    await Post(client, 14, "/answer correct");
                    await Post(client, 15, "/learn_preferences");
                }
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
                var transfer = await db.Set<Lesson>().SingleAsync(l => l.Transfer != null);
                Assert.Equal(source, transfer.Transfer!.SourceProgramId);
                Assert.Equal(target, transfer.ProgramId);
                Assert.Equal("Algebra applies to mechanics", transfer.Transfer.Reason);
                Assert.Equal("detailed", transfer.Teaching!.Preferences.Depth);
                Assert.Equal(restart == 0 ? LessonStatus.Paused : LessonStatus.Completed, transfer.Status);
                Assert.Equal(4, await db.Set<LearningPreference>().CountAsync());
                Assert.Equal(restart == 0 ? 1 : restart == 1 ? 2 : 3, await db.Set<LearningEvidence>().CountAsync());
                var replies = await db.TelegramOutboxMessages.ToListAsync();
                Assert.All(replies, r => Assert.Equal(OutboxMessageStatus.Pending, r.Status));
                Assert.Contains(replies, r => r.Text.Contains("Basis: Algebra applies to mechanics", StringComparison.Ordinal));
            }
            Assert.Equal(2, teacher.Requests.Count);
            Assert.Equal("brief", teacher.Requests[1].Personalization!.ExplicitPreferences.Depth);
            Assert.Equal("dialogue", teacher.Requests[1].Personalization!.ExplicitPreferences.Format);
            Assert.Empty(teacher.Requests[1].Personalization!.StrategyEvidence);
            await using (var rollback = new OwnDayDbContext(options))
            {
                await rollback.GetService<IMigrator>().MigrateAsync(previous);
                await rollback.Database.MigrateAsync();
            }
            await using (var restored = new OwnDayDbContext(options))
            {
                Assert.Equal(4, await restored.Set<Lesson>().CountAsync());
                Assert.Equal(3, await restored.Set<LearningEvidence>().CountAsync());
                Assert.Empty(await restored.Set<LearningPreference>().ToListAsync());
                Assert.All(await restored.Set<Lesson>().ToListAsync(), l =>
                {
                    Assert.Null(l.Teaching);
                    Assert.Null(l.Transfer);
                });
            }
        }
        finally
        {
#pragma warning disable EF1003 // Schema name is generated from a GUID.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    private static Task Post(HttpClient client, int id, string command) => TelegramCurriculumTests.PostAsync(client, id, 101, command);
}
