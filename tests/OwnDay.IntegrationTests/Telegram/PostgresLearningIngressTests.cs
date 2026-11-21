using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Start_WorkerWithMissingMigration_RejectsBeforeAcceptingTraffic()
    {
        await using (var db = Open())
            await db.GetService<IMigrator>().MigrateAsync("20261112140000_AddLearningProviderSteps");
        var probe = new Probe("");
        using var host = Host(probe, worker: true, publicIngress: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => host.CreateClient()));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_PausedAcceptance_PreservesAcceptedWorkAndAcknowledgesDuplicates()
    {
        var probe = new Probe("");
        using (var host = Host(probe, worker: false, publicIngress: true, configure: options => options.Enabled = true))
        using (var client = host.CreateClient())
        using (var response = await PostPublicAsync(client, 1, "/learn English")) response.EnsureSuccessStatusCode();
        using (var host = Host(probe, worker: false, publicIngress: true,
            configure: options => { options.Enabled = true; options.AcceptancePaused = true; }))
        using (var client = host.CreateClient())
        {
            using var duplicate = await PostPublicAsync(client, 1, "/learn English");
            duplicate.EnsureSuccessStatusCode();
            using var rejected = await PostPublicAsync(client, 2, "/learn_pause");
            Assert.False(rejected.IsSuccessStatusCode);
        }
        await using var observer = Open();
        Assert.Single(await observer.ProcessedTelegramUpdates.ToListAsync());
        Assert.Equal(LearningRequestStatus.Ready, (await observer.Set<LearningRequest>().SingleAsync()).Status);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Start_DisabledProducerWithPendingWork_RejectsConfiguration()
    {
        await EnqueueAsync("learn", "English");
        var probe = new Probe("");
        using var host = Host(probe, worker: false, publicIngress: true, configure: options =>
        {
            options.Enabled = false;
            options.DurableIngressEnabled = false;
        });
        Assert.Throws<OptionsValidationException>(() => host.CreateClient());
        await using var observer = Open();
        Assert.Empty(await observer.ProcessedTelegramUpdates.ToListAsync());
        Assert.Single(await observer.Set<LearningRequest>().ToListAsync());
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_OverdueBacklog_ClosesNewAcceptanceAndReportsQueueAge()
    {
        await EnqueueAsync("learn", "English");
        await using (var db = Open())
            await db.Database.ExecuteSqlRawAsync("UPDATE learning_requests SET received_at = clock_timestamp() - interval '10 minutes'");
        var probe = new Probe("");
        using var host = Host(probe, worker: false, publicIngress: true, configure: options => options.Enabled = true);
        using var client = host.CreateClient();
        using var response = await PostPublicAsync(client, 2, "/learn_pause");
        Assert.False(response.IsSuccessStatusCode);
        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["learning_queue"].Status);
        Assert.Equal(1L, report.Entries["learning_queue"].Data["pending"]);
        using var health = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        var payload = await health.Content.ReadAsStringAsync();
        Assert.Contains("oldest_age_seconds", payload);
        await using var observer = Open();
        Assert.Empty(await observer.ProcessedTelegramUpdates.ToListAsync());
        Assert.Single(await observer.Set<LearningRequest>().ToListAsync());
    }

    [Fact]
    public void Configure_DurableIngressWithoutEnabledWorker_RejectsOptions()
    {
        var probe = new Probe("");
        using var host = Host(probe, worker: false, publicIngress: true, configure: options => options.Enabled = false);
        Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<LearningRequestProcessingOptions>>().Value);
    }

    [Fact]
    public async Task Post_QueuedAnswerForReplacedLesson_DoesNotAssessNewQuestion()
    {
        Guid oldLessonId;
        await using (var db = Open())
        {
            var old = Lesson.Start(_user, "Old topic", "Old theory", "Old question");
            oldLessonId = old.Id;
            db.Add(old);
            await db.SaveChangesAsync();
        }
        var probe = new Probe("");
        using (var host = Host(probe, worker: false, publicIngress: true))
        using (var client = host.CreateClient())
        {
            using var accepted = await PostPublicAsync(client, 1, "/answer old answer");
            accepted.EnsureSuccessStatusCode();
        }
        // Replace the bound question after durable receipt and before execution.
        await using (var db = Open())
        {
            (await db.Set<Lesson>().SingleAsync()).Finish();
            db.Add(Lesson.Start(_user, "New topic", "New theory", "New question"));
            await db.SaveChangesAsync();
        }
        using var restarted = Host(probe, worker: true, publicIngress: true);
        using var restartClient = restarted.CreateClient();
        await WaitAllPublicAsync(1);
        await using var observer = Open();
        Assert.Empty(await observer.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(0, probe.Calls);
        Assert.Equal("New topic", (await observer.Set<Lesson>().SingleAsync(lesson => lesson.Status == LessonStatus.AwaitingAnswer)).Topic);
        var request = await observer.Set<LearningRequest>().SingleAsync();
        Assert.Equal("dialogue_reply", request.Outcome);
        using var input = System.Text.Json.JsonDocument.Parse(request.NormalizedInput);
        Assert.Equal(oldLessonId, input.RootElement.GetProperty("lessonId").GetGuid());
    }

    [Fact]
    public async Task Post_CommandJourneyAcrossRestart_PreservesQuestionAndCreatesOneEvidence()
    {
        var probe = new Probe("");
        probe.Release.TrySetResult();
        Guid lessonId;
        using (var host = Host(probe, worker: true, publicIngress: true))
        using (var client = host.CreateClient())
        {
            foreach (var (id, command) in new[] { (1, "/learn_request English | Work emails"), (2, "/learn_next"),
                (3, "/learn_next"), (4, "/learn_ask Explain the question"), (5, "/learn_pause") })
            {
                using var response = await PostPublicAsync(client, id, command);
                response.EnsureSuccessStatusCode();
                await WaitAllPublicAsync(id);
            }
            await using var observer = Open();
            var lesson = await observer.Set<Lesson>().Include(l => l.Interactions).SingleAsync();
            Assert.Equal(LessonStatus.Paused, lesson.Status);
            Assert.Single(lesson.Interactions);
            Assert.Empty(await observer.Set<LearningEvidence>().ToListAsync());
            lessonId = lesson.Id;
        }
        using (var host = Host(probe, worker: true, publicIngress: true))
        using (var client = host.CreateClient())
        {
            foreach (var (id, command) in new[] { (6, "/learn_resume"), (7, "/answer My answer"), (7, "/answer My answer") })
            {
                using var response = await PostPublicAsync(client, id, command);
                response.EnsureSuccessStatusCode();
                await WaitAllPublicAsync(id);
            }
        }
        await using var result = Open();
        Assert.Single(await result.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(lessonId, (await result.Set<Lesson>().SingleAsync()).Id);
        Assert.Equal(1, probe.KindCalls["generate"]);
        Assert.Equal(1, probe.KindCalls["assess"]);
    }

    private async Task WaitAllPublicAsync(int count)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            await using var db = Open();
            if (await db.Set<LearningRequest>().CountAsync(r => r.Status == LearningRequestStatus.Completed, deadline.Token) == count) return;
            await Task.Delay(25, deadline.Token);
        }
    }

    [Fact]
    public async Task Post_PublicLearningIngress_AcknowledgesDurableReceiptWhileProviderIsBlocked()
    {
        var probe = new Probe("generate");
        using var host = Host(probe, worker: true, publicIngress: true);
        using var client = host.CreateClient();
        try
        {
            using var response = await PostPublicAsync(client, 1, "/learn English").WaitAsync(TimeSpan.FromSeconds(5));
            response.EnsureSuccessStatusCode();
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var duplicate = await PostPublicAsync(client, 1, "/learn English");
            duplicate.EnsureSuccessStatusCode();
            await using var observer = Open();
            observer.Database.SetCommandTimeout(3);
            await using var transaction = await observer.Database.BeginTransactionAsync();
            await observer.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({_user.Value})");
            Assert.Single(await observer.Set<LearningRequest>().ToListAsync());
            Assert.Single(await observer.ProcessedTelegramUpdates.ToListAsync());
            Assert.Empty(await observer.Set<Lesson>().ToListAsync());
            Assert.Empty(await observer.TelegramOutboxMessages.ToListAsync());
        }
        finally { probe.Release.TrySetResult(); }
        await WaitCompletedAsync();
        Assert.Equal(1, probe.Calls);
    }

    [Theory]
    [InlineData("learn")]
    [InlineData("answer")]
    [InlineData("learn_ask")]
    [InlineData("learn_explain")]
    [InlineData("learn_pause")]
    [InlineData("learn_resume")]
    [InlineData("learn_finish")]
    [InlineData("learn_request")]
    [InlineData("learn_program")]
    [InlineData("learn_project")]
    [InlineData("learn_diagnose")]
    [InlineData("learn_next")]
    [InlineData("learn_adapt")]
    [InlineData("learn_focus")]
    [InlineData("learn_select")]
    [InlineData("learn_link")]
    [InlineData("learn_project_practice")]
    [InlineData("learn_transfer")]
    [InlineData("learn_practice")]
    [InlineData("learn_review")]
    [InlineData("learn_review_later")]
    [InlineData("learn_review_skip")]
    [InlineData("learn_preference")]
    [InlineData("learn_preference_reset")]
    [InlineData("learn_schedule")]
    [InlineData("learn_notify_pause")]
    [InlineData("learn_notify_resume")]
    [InlineData("learn_invite_start")]
    [InlineData("learn_invite_later")]
    [InlineData("learn_invite_skip")]
    public async Task Post_EachLearningWriter_CommitsReadyInputWithoutInlineExecution(string command)
    {
        var probe = new Probe("generate");
        using var host = Host(probe, worker: false, publicIngress: true, configure: options => options.Enabled = true);
        using var client = host.CreateClient();
        using var response = await PostPublicAsync(client, 1, "/" + command + "@OwnDayBot original | arguments");
        response.EnsureSuccessStatusCode();
        await using var observer = Open();
        var request = await observer.Set<LearningRequest>().SingleAsync();
        Assert.Equal(LearningRequestStatus.Ready, request.Status);
        Assert.Contains("original | arguments", request.NormalizedInput);
        Assert.Null(request.CompletedAt);
        Assert.Empty(await observer.TelegramOutboxMessages.ToListAsync());
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_ReceiptCommitFails_RollsBackAndRedeliverySurvivesRestart()
    {
        var probe = new Probe("generate");
        using (var host = Host(probe, worker: false, publicIngress: true,
            configure: options => options.Enabled = true, interceptor: new RejectReceiptCommit()))
        using (var client = host.CreateClient())
        using (var response = await PostPublicAsync(client, 1, "/learn English"))
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using (var observer = Open())
        {
            Assert.Empty(await observer.Set<LearningRequest>().ToListAsync());
            Assert.Empty(await observer.ProcessedTelegramUpdates.ToListAsync());
        }
        using (var host = Host(probe, worker: false, publicIngress: true, configure: options => options.Enabled = true))
        using (var client = host.CreateClient())
        using (var response = await PostPublicAsync(client, 1, "/learn English")) response.EnsureSuccessStatusCode();
        probe.Release.TrySetResult();
        using var restarted = Host(probe, worker: true, publicIngress: true);
        using var restartClient = restarted.CreateClient();
        await WaitCompletedAsync();
        Assert.Equal(1, probe.Calls);
    }

    private static Task<HttpResponseMessage> PostPublicAsync(HttpClient client, int id, string text, int? messageId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        request.Content = JsonContent.Create(new
        {
            update_id = id,
            message = new { message_id = messageId ?? id, date = 1700000000, chat = new { id = 101, type = "private" },
                from = new { id = 101, is_bot = false, first_name = "Test" }, text }
        });
        return client.SendAsync(request);
    }

    private sealed class RejectReceiptCommit : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected receipt commit failure.");
    }
}
