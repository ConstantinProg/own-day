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

public sealed class TelegramKnowledgeTests
{
    [Fact]
    public async Task Post_SkillPages_BoundsRepliesAndKeepsOwnerSkillsReachable()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var skills = Enumerable.Range(0, 23)
            .Select(i => Skill.Create(new UserId(101), $"Skill {i:D2}")).ToArray();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            db.AddRange(skills);
            db.Add(Skill.Create(new UserId(202), "Foreign skill"));
            await db.SaveChangesAsync();
        }

        await TelegramCurriculumTests.PostAsync(client, 1, 101, "/learn_skills");
        await TelegramCurriculumTests.PostAsync(client, 2, 101, "/learn_skills 2");
        await TelegramCurriculumTests.PostAsync(client, 3, 101, "/learn_skills 3");
        await TelegramCurriculumTests.PostAsync(client, 4, 101, "/learn_skills 4");
        var invalidArguments = new[] { "0", "-1", "abc", "2147483647", "999999999999" };
        for (var index = 0; index < invalidArguments.Length; index++)
            await TelegramCurriculumTests.PostAsync(client, 5 + index, 101, "/learn_skills " + invalidArguments[index]);

        using var verification = factory.Services.CreateScope();
        var context = verification.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var replies = await context.TelegramOutboxMessages.ToListAsync();
        Assert.Equal(9, replies.Count);
        Assert.All(replies, reply => Assert.InRange(reply.Text.Length, 1, 4096));
        var pages = replies.Where(r => r.Text.StartsWith("Skills", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, pages.Length);
        Assert.Contains(pages, r => r.Text.Contains("Next: /learn_skills 2", StringComparison.Ordinal));
        Assert.DoesNotContain(pages, r => r.Text.Contains("Next: /learn_skills 4", StringComparison.Ordinal));
        var text = string.Join("\n", pages.Select(r => r.Text));
        Assert.All(skills, skill => Assert.Equal(1, pages.Count(r => r.Text.Contains($"Skill: {skill.Id}", StringComparison.Ordinal))));
        Assert.DoesNotContain("Foreign skill", text);
        Assert.Contains(replies, r => r.Text.StartsWith("No skills on this page.", StringComparison.Ordinal));
        Assert.Equal(5, replies.Count(r => r.Text.StartsWith("Use /learn_skills", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Post_AnswersAndPractice_PreservesFactsAndExplainsContradictoryAssessments()
    {
        using var factory = new OwnDayHostFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(new TelegramCurriculumTests.Teacher()))));
        using var client = host.CreateClient();
        await TelegramCurriculumTests.PostAsync(client, 1, 101, "/learn Fractions");
        Guid skillId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            skillId = (await db.Set<Skill>().SingleAsync()).Id;
        }
        await TelegramCurriculumTests.PostAsync(client, 2, 101, $"/learn_status {skillId}");
        await TelegramCurriculumTests.PostAsync(client, 3, 101, "/answer incorrect");
        await TelegramCurriculumTests.PostAsync(client, 3, 101, "/answer incorrect");
        await TelegramCurriculumTests.PostAsync(client, 4, 101, "/answer correct"); // Ended lesson cannot receive another answer.
        await TelegramCurriculumTests.PostAsync(client, 5, 101, $"/learn_practice {skillId}");
        await TelegramCurriculumTests.PostAsync(client, 6, 101, "/answer incorrect");
        await TelegramCurriculumTests.PostAsync(client, 7, 101, $"/learn_practice {skillId}");
        await TelegramCurriculumTests.PostAsync(client, 8, 101, "/answer correct");
        await TelegramCurriculumTests.PostAsync(client, 9, 101, $"/learn_status {skillId}");
        await TelegramCurriculumTests.PostAsync(client, 10, 101, "/learn_skills");
        await TelegramCurriculumTests.PostAsync(client, 11, 202, $"/learn_status {skillId}");
        await TelegramCurriculumTests.PostAsync(client, 12, 202, $"/learn_practice {skillId}");
        await TelegramCurriculumTests.PostAsync(client, 13, 202, "/learn_skills");
        await TelegramCurriculumTests.PostAsync(client, 14, 101, "/learn Fractions"); // A new identity, even for the same title.
        await TelegramCurriculumTests.PostAsync(client, 15, 101, "/learn_finish");
        using var verification = host.Services.CreateScope();
        var context = verification.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(2, await context.Set<Skill>().CountAsync());
        var evidence = await context.Set<LearningEvidence>().ToListAsync();
        Assert.Equal(3, evidence.Count);
        Assert.All(evidence, e =>
        {
            Assert.Equal(skillId, e.SkillId);
            Assert.Equal(new UserId(101), e.UserId);
            Assert.Equal(e.AssessedCorrect ? "correct" : "incorrect", e.Answer);
            Assert.Equal("Feedback", e.AssessmentFeedback);
            Assert.Equal("LLM (unspecified model)", e.AssessmentSource);
            Assert.Equal("AI answer assessment", e.Method);
            Assert.Equal(TimeSpan.Zero, e.ObservedAt.Offset);
        });
        var status = await verification.ServiceProvider.GetRequiredService<KnowledgeService>()
            .StatusAsync(new UserId(101), skillId, CancellationToken.None);
        Assert.Equal(SkillMastery.Conflicting, status!.State.Mastery);
        Assert.True(status.State.RepeatedDifficulty);
        var replies = await context.TelegramOutboxMessages.ToListAsync();
        Assert.Contains(replies, r => r.Text.Contains("Unknown:", StringComparison.Ordinal));
        Assert.Contains(replies, r => r.Text.Contains("Conflicting:", StringComparison.Ordinal));
        Assert.Contains(replies, r => r.Text.Contains("Repeated difficulty:", StringComparison.Ordinal));
        Assert.Contains(replies, r => r.Text.Contains("Observed answer", StringComparison.Ordinal));
        Assert.Contains(replies, r => r.Text.Contains("AI interpretation", StringComparison.Ordinal));
        Assert.All(replies.Where(r => r.ChatId == 202), r => Assert.DoesNotContain("Fractions", r.Text));
        Assert.All(replies, r => Assert.InRange(r.Text.Length, 1, 4096));
    }

    [Fact]
    public async Task Post_ProgramRetry_ReusesTopicSkillAndKeepsEvidenceSeparate()
    {
        using var factory = new OwnDayHostFactory();
        var teacher = new TelegramCurriculumTests.Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
            services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(new TelegramCurriculumTests.Planner { NoQuestion = true }));
        }));
        using var client = host.CreateClient();
        await TelegramCurriculumTests.PostAsync(client, 1, 101, "/learn_program Algebra | Beginner");
        await TelegramCurriculumTests.PostAsync(client, 2, 101, "/learn_next");
        await TelegramCurriculumTests.PostAsync(client, 3, 101, "/answer incorrect");
        await TelegramCurriculumTests.PostAsync(client, 4, 101, "/learn_next");
        await TelegramCurriculumTests.PostAsync(client, 5, 101, "/answer correct");
        await TelegramCurriculumTests.PostAsync(client, 6, 101, "/learn_next");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var lessons = await db.Set<Lesson>().ToListAsync();
        Assert.Equal(2, await db.Set<Skill>().CountAsync());
        Assert.Single(lessons.Where(l => l.TopicCode == "basics").Select(l => l.SkillId).Distinct());
        Assert.Equal(2, await db.Set<LearningEvidence>().CountAsync());
        var skill = await db.Set<Skill>().SingleAsync(s => s.Title == "Basics");
        await TelegramCurriculumTests.PostAsync(client, 7, 101, "/learn_finish");
        await TelegramCurriculumTests.PostAsync(client, 8, 101, $"/learn_practice {skill.Id}");
        Assert.Contains("Mathematics", teacher.Requests.Last().Topic);
        Assert.Contains("Algebra", teacher.Requests.Last().Topic);
        Assert.Contains("Basics", teacher.Requests.Last().Topic);
    }

    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Post_KnowledgeAcrossRestarts_PreservesEvidenceAndMigrationCompatibility()
    {
        var connection = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required.");
        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new OwnDayDbContext(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connection).Options);
#pragma warning disable EF1003 // Schema is generated from a GUID.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003
        try
        {
            var scopedConnection = connection + ";Search Path=" + schema;
            var options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(scopedConnection).Options;
            await using (var setup = new OwnDayDbContext(options))
            {
                await setup.GetService<IMigrator>().MigrateAsync("20261105172909_AddLearningPrograms");
                await setup.Database.ExecuteSqlRawAsync("""
                    INSERT INTO learning_lessons ("Id", "UserId", "Topic", "Theory", "Question", "Status")
                    VALUES ('11111111-1111-1111-1111-111111111111', 101, 'Legacy', 'Theory', 'Question', 0)
                    """);
                await setup.Database.MigrateAsync();
                Assert.Null((await setup.Set<Lesson>().SingleAsync()).SkillId);
                Assert.Empty(await setup.Set<LearningEvidence>().ToListAsync());
            }
            Guid skillId = default;
            for (var restart = 0; restart < 3; restart++)
            {
                using var factory = new OwnDayHostFactory();
                using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<OwnDayDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<OwnDayDbContext>>();
                    services.AddDbContext<OwnDayDbContext>(builder => builder.UseNpgsql(scopedConnection));
                    services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(new TelegramCurriculumTests.Teacher()));
                }));
                using var client = host.CreateClient();
                if (restart == 0) await TelegramCurriculumTests.PostAsync(client, 1, 101, "/answer incorrect");
                else
                {
                    await TelegramCurriculumTests.PostAsync(client, 1, 101, "/answer incorrect");
                    await TelegramCurriculumTests.PostAsync(client, restart * 3, 101, $"/learn_status {skillId}");
                    if (restart == 1)
                    {
                        await TelegramCurriculumTests.PostAsync(client, 4, 101, $"/learn_practice {skillId}");
                        await TelegramCurriculumTests.PostAsync(client, 5, 101, "/answer correct");
                    }
                }
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
                skillId = (await db.Set<Skill>().SingleAsync()).Id;
                var evidence = await db.Set<LearningEvidence>().ToListAsync();
                Assert.Equal(restart == 0 ? 1 : 2, evidence.Count);
                Assert.All(evidence, e => Assert.Equal(skillId, e.SkillId));
                if (restart == 2) Assert.Equal(SkillMastery.Conflicting, LearnerSkillState.Evaluate(evidence).Mastery);
            }
            await using var rollback = new OwnDayDbContext(options);
            await rollback.GetService<IMigrator>().MigrateAsync("20261105172909_AddLearningPrograms");
            Assert.Equal(2, await rollback.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM learning_lessons").SingleAsync());
            await rollback.Database.MigrateAsync();
            Assert.All(await rollback.Set<Lesson>().ToListAsync(), l => Assert.Equal(LessonStatus.Completed, l.Status));
            Assert.Empty(await rollback.Set<LearningEvidence>().ToListAsync());
            Assert.Empty(await rollback.Set<Skill>().ToListAsync());
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }
}
