using System.Text.Json;
using System.Data.Common;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Learning;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Handling;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot.Types;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

[Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
public sealed class PostgresLearningWorkerTests : IAsyncLifetime
{
    private readonly string _schema = "ownday_test_" + Guid.NewGuid().ToString("N");
    private string _connection = string.Empty;

    public async Task InitializeAsync()
    {
        _connection = (Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required.")) + ";Search Path=" + _schema;
        await using var db = Open();
#pragma warning disable EF1003 // Isolated GUID schema.
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + _schema);
#pragma warning restore EF1003
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var db = Open();
#pragma warning disable EF1003 // Same isolated GUID schema.
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA " + _schema + " CASCADE");
#pragma warning restore EF1003
    }

    [Fact]
    public async Task Migrate_CompletedSynchronousRequest_PreservesTerminalMeaningWithoutSchedulingWork()
    {
        await using var db = Open();
        var migrations = db.Database.GetMigrations().ToArray();
        var leases = Array.FindIndex(migrations, migration => migration.EndsWith("_AddLearningRequestLeases", StringComparison.Ordinal));
        await db.GetService<IMigrator>().MigrateAsync(migrations[leases - 1]);
        var input = JsonSerializer.Serialize(new LearningCommandInput("learn_pause", ""), JsonSerializerOptions.Web);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO learning_requests (id, user_id, sequence, source_key, source_update_id, chat_id,
                payload_kind, payload_version, normalized_input, status, outcome, received_at, completed_at)
            VALUES ('cccccccc-cccc-cccc-cccc-cccccccccccc', 101, 1, 'telegram:update:1', 1, 101,
                'learning_command', 1, {input}::jsonb, 0, 'reply_committed', now(), now());
            """);
        await db.Database.MigrateAsync();
        var request = await db.Set<LearningRequest>().SingleAsync();
        Assert.Equal(LearningRequestStatus.Completed, request.Status);
        Assert.Equal("reply_committed", request.Outcome);
        Assert.Equal(0, request.AttemptCount);
        Assert.Null(request.LeaseToken);
        Assert.Empty(await db.Set<LearningAppliedOperation>().ToListAsync());
        using var host = Host(false);
        using var scope = host.Services.CreateScope();
        Assert.Null(await Store(scope).ClaimAsync(TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task Claim_TwoWorkers_ClaimsOnlyOneHeadAndAllowsOtherOwner()
    {
        await SeedAsync(101, 1, 1, "learn_pause");
        await SeedAsync(101, 2, 2, "learn_resume");
        using var host = Host(false);
        using var first = host.Services.CreateScope();
        using var second = host.Services.CreateScope();
        var claims = await Task.WhenAll(
            Store(first).ClaimAsync(TimeSpan.FromMinutes(1), default),
            Store(second).ClaimAsync(TimeSpan.FromMinutes(1), default));
        Assert.Single(claims, claim => claim is not null);
        await SeedAsync(202, 1, 3, "learn_finish");
        using var third = host.Services.CreateScope();
        Assert.Equal(202, (await Store(third).ClaimAsync(TimeSpan.FromMinutes(1), default))!.UserId.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claim_HeadInBackoffOrRunning_NeverSkipsToLaterRequest(bool running)
    {
        var id = await SeedAsync(101, 1, 1, "learn_pause");
        await SeedAsync(101, 2, 2, "learn_resume");
        using var host = Host(false);
        using var scope = host.Services.CreateScope();
        if (running)
        {
            Assert.NotNull(await Store(scope).ClaimAsync(TimeSpan.FromMinutes(1), default));
        }
        else
        {
            await using var db = Open();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE learning_requests SET available_at = clock_timestamp() + interval '1 hour' WHERE id = {id}");
        }
        using var next = host.Services.CreateScope();
        Assert.Null(await Store(next).ClaimAsync(TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task Process_ReclaimedLease_RejectsStaleOwnerAndCommitsOneEffectReplyAndMarker()
    {
        var id = await SeedAsync(101, 1, 1, "learn_pause");
        await using (var db = Open())
        {
            db.Add(Lesson.Start(new UserId(101), "Algebra", "Theory", "Question"));
            await db.SaveChangesAsync();
        }
        using var host = Host(false);
        using var old = host.Services.CreateScope();
        var expired = (await Store(old).ClaimAsync(TimeSpan.FromMinutes(1), default))!;
        await using (var db = Open())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE learning_requests SET lease_until = clock_timestamp() - interval '1 second' WHERE id = {id}");
        }
        using var current = host.Services.CreateScope();
        var lease = (await Store(current).ClaimAsync(TimeSpan.FromMinutes(1), default))!;
        Assert.NotEqual(expired.Token, lease.Token);
        Assert.False(await old.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(expired, 3, default));
        Assert.True(await current.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        using var repeat = host.Services.CreateScope();
        Assert.False(await repeat.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        await using var observer = Open();
        Assert.Equal(LessonStatus.Paused, (await observer.Set<Lesson>().SingleAsync()).Status);
        Assert.Single(await observer.Set<LearningAppliedOperation>().ToListAsync());
        Assert.Single(await observer.TelegramOutboxMessages.ToListAsync());
        Assert.Equal(LearningRequestStatus.Completed, (await observer.Set<LearningRequest>().SingleAsync()).Status);
    }

    [Fact]
    public async Task Apply_DifferentOwners_ProcessesWhileAnotherOwnerIsBlocked()
    {
        await SeedAsync(101, 1, 1, "learn_pause");
        await SeedAsync(202, 1, 2, "learn_resume");
        using var host = Host(false);
        using var first = host.Services.CreateScope();
        using var second = host.Services.CreateScope();
        var lease = (await Store(first).ClaimAsync(TimeSpan.FromMinutes(1), default))!;
        var other = (await Store(second).ClaimAsync(TimeSpan.FromMinutes(1), default))!;
        Assert.NotEqual(lease.UserId, other.UserId);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = Store(first).ApplyAsync(lease, async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return new("reply_committed", ["First owner reply"]);
        }, default);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await second.ServiceProvider.GetRequiredService<LearningRequestProcessor>()
                .ProcessAsync(other, 3, default).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(blocked.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await blocked.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Process_UnknownPayloadAndExhaustedAttempts_TerminatesWithoutProviderAndUnblocksOwner()
    {
        var poison = await SeedAsync(101, 1, 1, "answer");
        var exhausted = await SeedAsync(101, 2, 2, "learn_pause");
        await using (var db = Open())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE learning_requests SET attempt_count = 3 WHERE id = {exhausted}");
        }
        using var host = Host(false);
        for (var i = 0; i < 2; i++)
        {
            using var scope = host.Services.CreateScope();
            var lease = (await Store(scope).ClaimAsync(TimeSpan.FromMinutes(1), default))!;
            Assert.Equal(i == 0 ? poison : exhausted, lease.RequestId);
            Assert.True(await scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        }
        await using var observer = Open();
        Assert.All(await observer.Set<LearningRequest>().ToListAsync(), request => Assert.Equal(LearningRequestStatus.Completed, request.Status));
        Assert.Equal(2, await observer.TelegramOutboxMessages.CountAsync());
        Assert.Empty(await observer.Set<Lesson>().ToListAsync());
    }

    [Fact]
    public async Task Post_ReceiptCommittedThenRestart_RealHostedWorkerCompletesDespiteHttpTokenCancellation()
    {
        using var httpCancellation = new CancellationTokenSource();
        using (var receiving = Host(false, testIngress: true))
        using (var client = receiving.CreateClient())
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
            request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
            request.Content = JsonContent.Create(new
            {
                update_id = 1,
                message = new { message_id = 1, date = 1700000000, chat = new { id = 101, type = "private" },
                    from = new { id = 101, is_bot = false, first_name = "Learner" }, text = "/learn_pause" }
            });
            using var response = await client.SendAsync(request, httpCancellation.Token);
            response.EnsureSuccessStatusCode();
            httpCancellation.Cancel();
            await using var observer = Open();
            var accepted = await observer.Set<LearningRequest>().SingleAsync();
            Assert.Equal(LearningRequestStatus.Ready, accepted.Status);
            Assert.Empty(await observer.TelegramOutboxMessages.ToListAsync());
        }
        using (var restarted = Host(true))
        {
            _ = restarted.Services;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                await using var observer = Open();
                if (await observer.TelegramOutboxMessages.AnyAsync(deadline.Token)) break;
                await Task.Delay(50, deadline.Token);
            }
        }
        await using var db = Open();
        Assert.Equal(LearningRequestStatus.Completed, (await db.Set<LearningRequest>().SingleAsync()).Status);
        Assert.Single(await db.Set<LearningAppliedOperation>().ToListAsync());
        Assert.Single(await db.TelegramOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Process_CommitFailure_RollsBackMutationMarkerReplyAndCompletion()
    {
        var id = await SeedAsync(101, 1, 1, "learn_pause");
        await using (var db = Open())
        {
            db.Add(Lesson.Start(new UserId(101), "Algebra", "Theory", "Question"));
            await db.SaveChangesAsync();
        }
        using (var failing = Host(false, interceptor: new RejectCommit()))
        using (var scope = failing.Services.CreateScope())
        {
            var lease = (await Store(scope).ClaimAsync(TimeSpan.FromMinutes(1), default))!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
                .GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        }
        await using (var observer = Open())
        {
            Assert.Equal(LessonStatus.AwaitingAnswer, (await observer.Set<Lesson>().SingleAsync()).Status);
            Assert.Empty(await observer.TelegramOutboxMessages.ToListAsync());
            Assert.Empty(await observer.Set<LearningAppliedOperation>().ToListAsync());
            Assert.Equal(LearningRequestStatus.Running, (await observer.Set<LearningRequest>().SingleAsync()).Status);
            await observer.Database.ExecuteSqlInterpolatedAsync($"UPDATE learning_requests SET lease_until = clock_timestamp() - interval '1 second' WHERE id = {id}");
        }
        using var recovered = Host(false);
        using var recovery = recovered.Services.CreateScope();
        var retry = (await Store(recovery).ClaimAsync(TimeSpan.FromMinutes(1), default))!;
        Assert.True(await recovery.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(retry, 3, default));
        await using var final = Open();
        Assert.Equal(LessonStatus.Paused, (await final.Set<Lesson>().SingleAsync()).Status);
        Assert.Single(await final.Set<LearningAppliedOperation>().ToListAsync());
    }

    [Fact]
    public async Task Apply_LeaseExpiresDuringOperation_RollsBackAllChanges()
    {
        await SeedAsync(101, 1, 1, "learn_pause");
        await using (var db = Open())
        {
            db.Add(Lesson.Start(new UserId(101), "Algebra", "Theory", "Question"));
            await db.SaveChangesAsync();
        }
        using var host = Host(false);
        using var scope = host.Services.CreateScope();
        var lease = (await Store(scope).ClaimAsync(TimeSpan.FromMinutes(1), default))!;
        var applied = await Store(scope).ApplyAsync(lease, async (request, token) =>
        {
            Assert.IsType<LearningResult.Session>(await scope.ServiceProvider.GetRequiredService<LearningService>().PauseAsync(request.UserId, token));
            await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE learning_requests SET lease_until = clock_timestamp() - interval '1 second' WHERE id = {request.Id}", token);
            return new("reply_committed", ["Exercise paused"]);
        }, default);
        Assert.False(applied);
        await using var observer = Open();
        Assert.Equal(LessonStatus.AwaitingAnswer, (await observer.Set<Lesson>().SingleAsync()).Status);
        Assert.Empty(await observer.Set<LearningAppliedOperation>().ToListAsync());
        Assert.Empty(await observer.TelegramOutboxMessages.ToListAsync());
    }

    private OwnDayDbContext Open() => new(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(_connection).Options);
    private static EfLearningRequestStore Store(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<EfLearningRequestStore>();

    private async Task<Guid> SeedAsync(long user, long sequence, int update, string name)
    {
        await using var db = Open();
        var request = new LearningRequest
        {
            UserId = new UserId(user), Sequence = sequence, SourceKey = $"telegram:update:{update}",
            SourceUpdateId = update, SourceMessageId = update, ChatId = user, Status = LearningRequestStatus.Ready,
            Outcome = null, CompletedAt = null, ReceivedAt = DateTime.UtcNow, AvailableAt = DateTime.UtcNow,
            NormalizedInput = JsonSerializer.Serialize(new LearningCommandInput(name, ""), JsonSerializerOptions.Web)
        };
        db.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    private WebApplicationFactory<Program> Host(bool worker, bool testIngress = false, IInterceptor? interceptor = null) =>
        new OwnDayHostFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OwnDayDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<OwnDayDbContext>>();
            services.AddDbContext<OwnDayDbContext>(options =>
            {
                options.UseNpgsql(_connection);
                if (interceptor is not null) options.AddInterceptors(interceptor);
            });
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(new NoProviderTeacher()));
            // Keep startup configuration valid; worker controls hosted execution so tests can process manually.
            services.Configure<LearningRequestProcessingOptions>(options => { options.Enabled = true; options.PollMilliseconds = 20; });
            if (worker) services.AddHostedService<LearningRequestWorker>();
            if (testIngress) services.Replace(ServiceDescriptor.Scoped<ITelegramUpdateHandler, TestIngress>());
        }));

    private sealed class NoProviderTeacher : ILessonTeacher
    {
        public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token) => throw new InvalidOperationException("Provider is forbidden.");
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token) => throw new InvalidOperationException("Provider is forbidden.");
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) => throw new InvalidOperationException("Provider is forbidden.");
    }

    private sealed class RejectCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected apply commit failure.");
    }

    // Isolated no-provider producer for worker acceptance tests. Production ingress stays synchronous.
    private sealed class TestIngress(OwnDayDbContext db, TelegramUpdateRouter router) : ITelegramUpdateHandler
    {
        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var route = Assert.IsType<TelegramUpdateRouteResult.Dispatch>(router.Route(update));
            Assert.Contains(route.Command.Name, new[] { "learn_pause", "learn_resume", "learn_finish" });
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var now = DateTime.UtcNow;
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO processed_telegram_updates (update_id, received_at) VALUES ({update.Id}, {now})
                ON CONFLICT DO NOTHING
                """, cancellationToken);
            if (inserted == 0) return;
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({route.Command.UserId.Value})", cancellationToken);
            var sequence = await db.Set<LearningRequest>().Where(r => r.UserId == route.Command.UserId)
                .MaxAsync(r => (long?)r.Sequence, cancellationToken) ?? 0;
            db.Add(new LearningRequest
            {
                UserId = route.Command.UserId, ChatId = route.ChatId, Sequence = sequence + 1,
                SourceUpdateId = update.Id, SourceMessageId = update.Message!.Id, SourceKey = $"telegram:update:{update.Id}",
                ReceivedAt = now, AvailableAt = now, Status = LearningRequestStatus.Ready, Outcome = null, CompletedAt = null,
                NormalizedInput = JsonSerializer.Serialize(new LearningCommandInput(route.Command.Name, route.Command.Arguments), JsonSerializerOptions.Web)
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }
}
