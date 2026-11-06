using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Delivery;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class PostgresLearningAcceptanceTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Post_FullLearningJourneyAcrossRestart_PreservesEvidenceAndExplainsProgressWithoutChangingWork()
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
            await using (var setup = new OwnDayDbContext(options)) await setup.Database.MigrateAsync();
            var clock = new TelegramReviewTests.Clock { Now = new(2026, 11, 6, 15, 0, 0, TimeSpan.Zero) };
            var teacher = new Teacher();
            var planner = new TelegramCurriculumTests.Planner();
            var teaching = new TelegramAdaptiveTeachingTests.Planner();
            var sender = new Sender();
            Guid original = default;
            Guid skill = default;
            Guid pausedLesson = default;
            long projectId = 0;
            long actionId = 0;
            var update = 0;
            for (var restart = 0; restart < 2; restart++)
            {
                using var factory = new OwnDayHostFactory();
                using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<OwnDayDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<OwnDayDbContext>>();
                    services.AddDbContext<OwnDayDbContext>(builder => builder.UseNpgsql(scopedConnection));
                    services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
                    services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
                    services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(planner));
                    services.Replace(ServiceDescriptor.Singleton<ITeachingPlanner>(teaching));
                    services.Replace(ServiceDescriptor.Singleton<ITelegramMessageSender>(sender));
                }));
                using var client = host.CreateClient();
                Task Post(string command) => TelegramCurriculumTests.PostAsync(client, ++update, 101, command);
                if (restart == 0)
                {
                    (projectId, actionId) = await TelegramProjectLearningTests.SeedAsync(host.Services, 101, "Build algebra tool", "Implement equation input");
                    await Post($"/learn_project {projectId} | Beginner");
                    await Post("/learn_diagnose x is 2");
                    teacher.Fail = true;
                    await Post("/learn_next");
                    using (var failureInspection = host.Services.CreateScope())
                        Assert.Empty(await failureInspection.ServiceProvider.GetRequiredService<OwnDayDbContext>().Set<Lesson>().ToListAsync());
                    teacher.Fail = false;
                    await Post("/learn_next");
                    await Post("/learn_ask Why does this work?");
                    await Post("/answer incorrect");
                    await TelegramCurriculumTests.PostAsync(client, update, 101, "/answer incorrect");
                    await Post("/learn_adapt");
                    await Post("/learn_pause");
                    using var scope = host.Services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
                    original = (await db.Set<LearningProgram>().SingleAsync()).Id;
                    skill = (await db.Set<Skill>().SingleAsync()).Id;
                    pausedLesson = (await db.Set<Lesson>().SingleAsync(l => l.Status == LessonStatus.Paused)).Id;
                    Assert.Single(await db.Set<LearningEvidence>().ToListAsync());
                    Assert.Single(teaching.Requests);
                }
                else
                {
                    await TelegramCurriculumTests.PostAsync(client, update, 101, "/learn_pause");
                    await Post("/learn_resume");
                    await Post("/answer correct");
                    await Post($"/learn_status {skill}");
                    clock.Now = clock.Now.AddDays(1);
                    await Post("/learn_reviews");
                    await Post($"/learn_review {skill}");
                    await Post("/answer correct");
                    planner.DifferentDiscipline = true;
                    await Post("/learn_program Mechanics | Beginner");
                    await Post("/learn_diagnose x is 2");
                    await Post($"/learn_link basics | {skill} | Algebra supports mechanics");
                    await Post($"/learn_transfer basics | {original} | basics");
                    await Post("/answer correct");
                    await Post("/learn_schedule Europe/Moscow | 1 | 18:00-20:00");
                    using (var scope = host.Services.CreateScope())
                    {
                        var dispatcher = scope.ServiceProvider.GetRequiredService<StudyInvitationDispatcher>();
                        await dispatcher.DispatchBatchAsync(CancellationToken.None);
                        await dispatcher.DispatchBatchAsync(CancellationToken.None);
                        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
                        var invitation = (await db.Set<StudySchedule>().SingleAsync()).InvitationId;
                        Assert.NotNull(invitation);
                        Assert.Single(await db.TelegramOutboxMessages.Where(m => m.StudyInvitationId == invitation).ToListAsync());
                    }
                    await Post($"/learn_select {original}");
                    await Post($"/learn_project_practice basics | {actionId}");
                    await Post("/answer correct");
                    await Post("/learn_project_report");
                    await Post("/learn_progress");
                    await Post("/learn_progress 2");
                    await TelegramCurriculumTests.PostAsync(client, update, 101, "/learn_progress 2");
                    await TelegramCurriculumTests.PostAsync(client, ++update, 202, "/learn_progress");
                    using var scope2 = host.Services.CreateScope();
                    var context = scope2.ServiceProvider.GetRequiredService<OwnDayDbContext>();
                    var evidence = await context.Set<LearningEvidence>().ToListAsync();
                    var lessons = await context.Set<Lesson>().ToListAsync();
                    Assert.Equal(5, lessons.Count);
                    Assert.Equal(LessonStatus.Completed, lessons.Single(l => l.Id == pausedLesson).Status);
                    Assert.Single(lessons, l => l.Transfer is not null);
                    Assert.Single(lessons, l => l.ProjectActionId == actionId);
                    Assert.Equal(5, evidence.Count);
                    Assert.Single(evidence, e => e.IsReview);
                    Assert.All(evidence, e => Assert.Equal(skill, e.SkillId));
                    Assert.Equal(SkillMastery.Conflicting, LearnerSkillState.Evaluate(evidence).Mastery);
                    var programs = await context.Set<LearningProgram>().Include(p => p.Routes).ToListAsync();
                    Assert.Equal(2, programs.Count);
                    Assert.Equal(3, programs.Single(p => p.Id == original).CurrentRoute.Version);
                    Assert.Equal("Build algebra tool", programs.Single(p => p.Id == original).Goal);
                    Assert.Equal(ProjectStatus.Active, (await context.Set<Project>().SingleAsync()).Status);
                    Assert.Equal(OwnDay.Domain.Actions.ActionStatus.Active, (await context.Actions.SingleAsync()).Status);
                    var replies = await context.TelegramOutboxMessages.ToListAsync();
                    var text = string.Join('\n', replies.Select(m => m.Text));
                    Assert.Contains("Teaching adjustment", text);
                    Assert.Contains("Return to: Basics", text);
                    Assert.Contains("Expected action: Answer the current question", text);
                    Assert.Contains("Source unavailable", text);
                    Assert.Contains("Conflicting", text);
                    Assert.Contains("Basis (excerpt): Criterion: match the reference", text);
                    Assert.Contains("Algebra supports mechanics", text);
                    Assert.Contains("Project link: #", text);
                    Assert.Contains("answers do not prove delivery", text);
                    Assert.All(replies, r => Assert.InRange(r.Text.Length, 1, 4096));
                    Assert.All(replies.Where(r => r.ChatId == 202), r => Assert.DoesNotContain("Build algebra tool", r.Text));
                    Assert.Equal(update, await context.ProcessedTelegramUpdates.CountAsync());
                    Assert.All(replies, r => Assert.Equal(OutboxMessageStatus.Pending, r.Status));
                    // Delivery fails independently of the committed learning state, then recovers.
                    var delivery = scope2.ServiceProvider.GetRequiredService<TelegramOutboxDeliveryService>();
                    await delivery.DeliverBatchAsync(CancellationToken.None);
                    Assert.Contains(await context.TelegramOutboxMessages.ToListAsync(), m => m.AttemptCount == 1 && m.Status == OutboxMessageStatus.Pending);
                    clock.Now = clock.Now.AddHours(2);
                    for (var batch = 0; batch < 100 && await context.TelegramOutboxMessages.AnyAsync(m => m.Status == OutboxMessageStatus.Pending); batch++)
                        await delivery.DeliverBatchAsync(CancellationToken.None);
                    Assert.False(await context.TelegramOutboxMessages.AnyAsync(m => m.Status == OutboxMessageStatus.Pending));
                    Assert.Contains(sender.Delivered, t => t.Contains("Learning progress", StringComparison.Ordinal));
                    Assert.Equal(5, await context.Set<LearningEvidence>().CountAsync());
                }
            }
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    private sealed class Teacher : ILessonTeacher
    {
        public bool Fail { get; set; }
        public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Fail) throw new LessonProviderException();
            return Task.FromResult(new LessonMaterial("Theory", "Question")
            {
                ExpectedAnswer = "correct",
                Provenance = new("test-model", "RFC 9110", "test-version", null, null, MaterialSourceStatus.Unavailable)
            });
        }
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new LessonAssessment("Criterion: match the reference; this is a provisional assessment.", answer == "correct") { Source = "test-model" });
        }
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new LessonExplanation("An alternative example, then return to the question."));
        }
    }

    private sealed class Sender : ITelegramMessageSender
    {
        private bool _failed;
        public List<string> Delivered { get; } = [];
        public Task SendMessageAsync(long chatId, string text, string? replyMarkupJson, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_failed)
            {
                _failed = true;
                throw new HttpRequestException("Temporary test delivery failure");
            }
            Delivered.Add(text);
            return Task.CompletedTask;
        }
    }
}
