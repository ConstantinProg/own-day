using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramReviewTests
{
    [Fact]
    public async Task Post_ClockRegressionDuringAnswer_RollsBackWholeUpdateAndAllowsRetry()
    {
        using var factory = new OwnDayHostFactory();
        var clock = new Clock();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, clock, new Teacher())));
        using var client = host.CreateClient();
        await Post(client, 1, 101, "/learn fractions");
        await Post(client, 2, 101, "/answer correct");
        Guid skillId;
        using (var scope = host.Services.CreateScope())
            skillId = (await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().Set<Skill>().SingleAsync()).Id;
        await Post(client, 3, 101, $"/learn_practice {skillId}");
        clock.Now = clock.Now.AddMinutes(-1);
        await Post(client, 4, 101, "/answer correct", HttpStatusCode.InternalServerError);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            Assert.Single(await db.Set<Lesson>().Where(l => l.Status == LessonStatus.AwaitingAnswer).ToListAsync());
            Assert.Single(await db.Set<LearningEvidence>().ToListAsync());
            Assert.Equal(3, await db.ProcessedTelegramUpdates.CountAsync());
        }
        clock.Now = clock.Now.AddMinutes(2);
        await Post(client, 4, 101, "/answer correct");
        using var inspection = host.Services.CreateScope();
        var final = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(2, await final.Set<LearningEvidence>().CountAsync());
        Assert.Equal(4, await final.ProcessedTelegramUpdates.CountAsync());
        Assert.Equal(clock.Now.AddMinutes(-1).AddDays(1), (await final.Set<ReviewSchedule>().SingleAsync()).DueAt);
    }

    [Fact]
    public async Task Post_DelayedReview_UsesExistingSkillUpdatesKnowledgeAndDeduplicates()
    {
        using var factory = new OwnDayHostFactory();
        var clock = new Clock();
        var teacher = new Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, clock, teacher)));
        using var client = host.CreateClient();
        await Post(client, 1, 101, "/learn fractions");
        await Post(client, 2, 101, "/answer correct");
        Guid skillId;
        using (var scope = host.Services.CreateScope())
            skillId = (await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().Set<Skill>().SingleAsync()).Id;
        await Post(client, 3, 101, $"/learn_review {skillId}"); // Not due: no generation.
        await Post(client, 4, 101, "/learn_reviews");
        clock.Now = clock.Now.AddDays(100);
        await Post(client, 5, 101, "/learn_reviews");
        await Post(client, 6, 101, "/learn_reviews"); // Queries never enqueue more work.
        await Post(client, 7, 202, $"/learn_review {skillId}");
        await Post(client, 8, 202, "/learn_reviews");
        await Post(client, 9, 101, $"/learn_review {skillId}");
        await Post(client, 9, 101, $"/learn_review {skillId}");
        await Post(client, 10, 101, $"/learn_review {skillId}"); // Another update resumes the same lesson.
        await Post(client, 11, 101, $"/learn_review_skip {skillId}"); // Cannot skip an active review.
        await Post(client, 12, 101, "/learn_pause");
        await Post(client, 13, 101, "/answer wrong"); // Paused: no assessment.
        await Post(client, 14, 101, "/learn_resume");
        await Post(client, 15, 101, "/answer wrong");
        await Post(client, 15, 101, "/answer wrong");
        await Post(client, 16, 101, $"/learn_status {skillId}");
        using var inspection = host.Services.CreateScope();
        var db = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Single(await db.Set<Skill>().ToListAsync());
        var lessons = await db.Set<Lesson>().ToListAsync();
        var review = Assert.Single(lessons, l => l.IsReview);
        Assert.Equal(skillId, review.SkillId);
        Assert.Null(review.ProgramId);
        Assert.Equal("A fresh application question", review.Question);
        var evidence = await db.Set<LearningEvidence>().ToListAsync();
        Assert.Equal(2, evidence.Count);
        Assert.Single(evidence, e => e.IsReview && e.SkillId == skillId && !e.AssessedCorrect);
        Assert.Equal(SkillMastery.Conflicting, LearnerSkillState.Evaluate(evidence).Mastery);
        var schedule = Assert.Single(await db.Set<ReviewSchedule>().ToListAsync());
        Assert.Equal(clock.Now.AddHours(1), schedule.DueAt);
        Assert.Equal(review.Id, schedule.LastLessonId);
        Assert.Equal(0, schedule.SuccessfulReviews);
        Assert.Equal(2, teacher.Requests.Count);
        Assert.False(teacher.Requests[0].IsReview);
        Assert.True(teacher.Requests[1].IsReview);
        Assert.Equal(2, teacher.Assessed);
        Assert.Equal(16, await db.ProcessedTelegramUpdates.CountAsync());
        var replies = await db.TelegramOutboxMessages.ToListAsync();
        Assert.Equal(2, replies.Count(m => m.Text.StartsWith("Reviews due:", StringComparison.Ordinal)));
        Assert.All(replies.Where(m => m.ChatId == 202), m => Assert.DoesNotContain("fractions", m.Text));
        Assert.All(replies, m => Assert.InRange(m.Text.Length, 1, 4096));
        Assert.Contains(replies, m => m.Text.Contains("Conflicting", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 7)]
    public async Task Post_DeferOrSkip_ChangesOnlyOneDeadlineWithoutPenalty(bool skip, int days)
    {
        using var factory = new OwnDayHostFactory();
        var clock = new Clock();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, clock, new Teacher())));
        using var client = host.CreateClient();
        await Post(client, 1, 101, "/learn fractions");
        await Post(client, 2, 101, "/answer correct");
        Guid skillId;
        using (var scope = host.Services.CreateScope())
            skillId = (await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().Set<Skill>().SingleAsync()).Id;
        clock.Now = clock.Now.AddDays(5);
        var command = skip ? "learn_review_skip" : "learn_review_later";
        await Post(client, 3, 202, $"/{command} {skillId}");
        await Post(client, 4, 101, $"/{command} {skillId}");
        await Post(client, 4, 101, $"/{command} {skillId}");
        await Post(client, 5, 101, $"/{command} {skillId}");
        using var inspection = host.Services.CreateScope();
        var db = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var schedule = await db.Set<ReviewSchedule>().SingleAsync();
        Assert.Equal(clock.Now.AddDays(days), schedule.DueAt);
        Assert.Equal(0, schedule.SuccessfulReviews);
        Assert.Single(await db.Set<Lesson>().ToListAsync());
        Assert.Single(await db.Set<LearningEvidence>().ToListAsync());
        Assert.Single(await db.TelegramOutboxMessages.ToListAsync(), m => m.Text.StartsWith("Review rescheduled", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("invalid")]
    public async Task Post_ReviewGenerationRejected_KeepsDueDateAndDoesNotCreateEvidence(string failure)
    {
        using var factory = new OwnDayHostFactory();
        var clock = new Clock();
        var teacher = new Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, clock, teacher)));
        using var client = host.CreateClient();
        await Post(client, 1, 101, "/learn fractions");
        await Post(client, 2, 101, "/answer correct");
        Guid skillId;
        using (var scope = host.Services.CreateScope())
            skillId = (await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().Set<Skill>().SingleAsync()).Id;
        clock.Now = clock.Now.AddDays(1);
        teacher.Failure = failure;
        await Post(client, 3, 101, $"/learn_review {skillId}");
        using var inspection = host.Services.CreateScope();
        var db = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(clock.Now, (await db.Set<ReviewSchedule>().SingleAsync()).DueAt);
        Assert.Single(await db.Set<Lesson>().ToListAsync());
        Assert.Single(await db.Set<LearningEvidence>().ToListAsync());
    }

    [Theory]
    [InlineData("/learn_reviews 0")]
    [InlineData("/learn_reviews 2147483647")]
    [InlineData("/learn_reviews bad")]
    [InlineData("/learn_review bad")]
    [InlineData("/learn_review_skip")]
    [InlineData("/learn_review_later 00000000-0000-0000-0000-000000000000")]
    public async Task Post_InvalidReviewCommand_ReturnsUsageWithoutMutation(string command)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await Post(client, 1, 101, command);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.Set<ReviewSchedule>().ToListAsync());
        Assert.Contains("Use /learn_review", (await db.TelegramOutboxMessages.SingleAsync()).Text);
    }

    internal static void Configure(IServiceCollection services, Clock clock, Teacher teacher)
    {
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
        services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
    }

    internal static async Task Post(HttpClient client, int id, long user, string text, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        request.Content = JsonContent.Create(new
        {
            update_id = id,
            message = new { message_id = id, date = 1700000000, chat = new { id = user, type = "private" },
                from = new { id = user, is_bot = false, first_name = "Learner" }, text }
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class Teacher : ILessonTeacher
    {
        public List<LessonRequest> Requests { get; } = [];
        public int Assessed { get; private set; }
        public string Failure { get; set; } = "";
        public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Failure == "failure") throw new LessonProviderException();
            return Task.FromResult(new LessonMaterial(Failure == "invalid" ? "" : "Context",
                request.IsReview ? "A fresh application question" : "Original question"));
        }
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assessed++;
            return Task.FromResult(new LessonAssessment("AI basis", answer == "correct") { Source = "test-model" });
        }
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
