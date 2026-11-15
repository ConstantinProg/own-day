using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Handling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

[Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
public sealed class PostgresLearningRequestTests : IAsyncLifetime
{
    private readonly string _schema = "ownday_test_" + Guid.NewGuid().ToString("N");
    private string _connection = string.Empty;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required.");
        _connection = connection + ";Search Path=" + _schema;
        await using var db = Open();
#pragma warning disable EF1003 // Isolated schema name contains only a fixed prefix and a GUID.
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + _schema);
#pragma warning restore EF1003
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var db = Open();
#pragma warning disable EF1003 // Same generated schema as InitializeAsync.
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA " + _schema + " CASCADE");
#pragma warning restore EF1003
    }

    [Fact]
    public async Task Post_LearningAndRedeliveryAcrossRestart_CommitsOneTerminalRequestWithOrderedReplies()
    {
        var teacher = new TelegramCurriculumTests.Teacher();
        for (var restart = 0; restart < 2; restart++)
        {
            using var factory = Host(teacher);
            using var client = factory.CreateClient();
            await TelegramCurriculumTests.PostAsync(client, 1, 101, "/learn algebra");
            if (restart == 0)
            {
                await TelegramCurriculumTests.PostAsync(client, 2, 101, "/learn_pause");
                await TelegramCurriculumTests.PostAsync(client, 3, 202, "/learn geometry");
                await TelegramCurriculumTests.PostAsync(client, 4, 101, "/ping");
            }
        }
        await using var db = Open();
        Assert.Equal(3, await CountRequests(db));
        Assert.Equal(2, await db.Set<Lesson>().CountAsync());
        Assert.Equal(2, teacher.Generated);
        Assert.Equal(4, await db.ProcessedTelegramUpdates.CountAsync());
        Assert.Equal(3, await db.Database.SqlQueryRaw<long>("""
            SELECT count(*) AS "Value" FROM learning_requests
            WHERE status = 0 AND outcome = 'reply_committed' AND completed_at >= received_at
            """).SingleAsync());
        Assert.Equal(new long[] { 1, 2 }, await db.Database.SqlQueryRaw<long>("""
            SELECT sequence AS "Value" FROM learning_requests WHERE user_id = 101 ORDER BY sequence
            """).ToArrayAsync());
        var payload = await db.Database.SqlQueryRaw<string>("""
            SELECT normalized_input::text AS "Value" FROM learning_requests WHERE source_key = 'telegram:update:1'
            """).SingleAsync();
        using var json = JsonDocument.Parse(payload);
        Assert.Equal("learn", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("algebra", json.RootElement.GetProperty("arguments").GetString());
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("""
            SELECT count(*) AS "Value" FROM (
                SELECT r.id FROM learning_requests r
                LEFT JOIN telegram_outbox_messages m ON m.learning_request_id = r.id
                GROUP BY r.id HAVING count(m.id) = 0 OR min(m.reply_ordinal) <> 0
                    OR max(m.reply_ordinal) <> count(m.id) - 1
            ) invalid
            """).SingleAsync());
    }

    [Fact]
    public async Task Post_BlockedTeacher_DoesNotAcknowledgeOrExposePartialRequest()
    {
        var teacher = new BlockingTeacher();
        using var factory = Host(teacher);
        using var client = factory.CreateClient();
        var pending = TelegramCurriculumTests.PostAsync(client, 1, 101, "/learn algebra");
        try
        {
            await teacher.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(pending.IsCompleted);
            await using var db = Open();
            Assert.Equal(0, await CountRequests(db));
            Assert.Empty(await db.Set<Lesson>().ToListAsync());
            Assert.Empty(await db.ProcessedTelegramUpdates.ToListAsync());
            Assert.Empty(await db.TelegramOutboxMessages.ToListAsync());
        }
        finally
        {
            teacher.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(15));
        }
        await using var committed = Open();
        Assert.Equal(1, await CountRequests(committed));
        Assert.Single(await committed.Set<Lesson>().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_PersistenceFailureOrFailureAfterWrites_RollsBackEverythingAndAllowsRedelivery(bool afterWrites)
    {
        IInterceptor interceptor = afterWrites ? new RejectCommit() : new RejectSave();
        using (var factory = Host(new TelegramCurriculumTests.Teacher(), interceptor))
        using (var client = factory.CreateClient())
        {
            using var request = Request(1, "/learn algebra");
            using var failure = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        }
        await AssertEmptyAsync();
        using var recovered = Host(new TelegramCurriculumTests.Teacher());
        using var recoveredClient = recovered.CreateClient();
        await TelegramCurriculumTests.PostAsync(recoveredClient, 1, 101, "/learn algebra");
        await using var db = Open();
        Assert.Equal(1, await CountRequests(db));
        Assert.Single(await db.Set<Lesson>().ToListAsync());
    }

    [Fact]
    public async Task Handle_CancellationAfterProvider_RollsBackReceiptEffectAndReply()
    {
        using var cancellation = new CancellationTokenSource();
        var teacher = new TelegramCurriculumTests.Teacher { CancelAfterGenerate = cancellation };
        using (var factory = Host(teacher))
        {
            using var scope = factory.Services.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<ITelegramUpdateHandler>();
            var update = new Update
            {
                Id = 1,
                Message = new Message
                {
                    Id = 1, Chat = new Chat { Id = 101, Type = ChatType.Private },
                    From = new User { Id = 101, FirstName = "Learner" }, Text = "/learn algebra"
                }
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleAsync(update, cancellation.Token));
        }
        Assert.True(teacher.ReceivedToken.IsCancellationRequested);
        await AssertEmptyAsync();
        using var recovered = Host(new TelegramCurriculumTests.Teacher());
        using var client = recovered.CreateClient();
        await TelegramCurriculumTests.PostAsync(client, 1, 101, "/learn algebra");
        await using var db = Open();
        Assert.Equal(1, await CountRequests(db));
    }

    [Fact]
    public async Task Migrate_PopulatedLegacySchema_PreservesRowsWithoutInventingRequests()
    {
        await using var db = Open();
        await db.GetService<IMigrator>().MigrateAsync("20261106183140_AddLearningPersonalization");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO processed_telegram_updates (update_id, received_at, processed_at)
            VALUES (50, now(), now());
            INSERT INTO telegram_outbox_messages (id, chat_id, text, status, attempt_count, next_attempt_at, created_at)
            VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 101, 'Legacy pending reply', 0, 0, now(), now());
            """);
        // Seed only historical columns: the current EF model includes later additions.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO learning_lessons ("Id", "UserId", "Topic", "Theory", "Question", "Status")
            VALUES ({Guid.NewGuid()}, 101, 'Legacy algebra', 'Theory', 'Question', 0)
            """);
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(0, await CountRequests(db));
        Assert.Equal("Legacy algebra", (await db.Set<Lesson>().SingleAsync()).Topic);
        Assert.Equal("Legacy pending reply", (await db.TelegramOutboxMessages.SingleAsync()).Text);
        Assert.Equal(50, (await db.ProcessedTelegramUpdates.SingleAsync()).UpdateId);
        using var factory = Host(new TelegramCurriculumTests.Teacher());
        using var client = factory.CreateClient();
        await TelegramCurriculumTests.PostAsync(client, 50, 101, "/learn algebra");
        Assert.Equal(0, await CountRequests(db));
    }

    private OwnDayDbContext Open() => new(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(_connection).Options);

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host(ILessonTeacher teacher, IInterceptor? interceptor = null)
    {
        return new OwnDayHostFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OwnDayDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<OwnDayDbContext>>();
            services.AddDbContext<OwnDayDbContext>(options =>
            {
                options.UseNpgsql(_connection);
                if (interceptor is not null) options.AddInterceptors(interceptor);
            });
            services.Replace(ServiceDescriptor.Singleton(teacher));
        }));
    }

    private static Task<long> CountRequests(OwnDayDbContext db) => db.Database.SqlQueryRaw<long>(
        "SELECT count(*) AS \"Value\" FROM learning_requests").SingleAsync();

    private async Task AssertEmptyAsync()
    {
        await using var db = Open();
        Assert.Equal(0, await CountRequests(db));
        Assert.Empty(await db.Set<Lesson>().ToListAsync());
        Assert.Empty(await db.ProcessedTelegramUpdates.ToListAsync());
        Assert.Empty(await db.TelegramOutboxMessages.ToListAsync());
    }

    private static HttpRequestMessage Request(int id, string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        request.Content = JsonContent.Create(new
        {
            update_id = id,
            message = new { message_id = id, date = 1700000000, chat = new { id = 101, type = "private" },
                from = new { id = 101, is_bot = false, first_name = "Learner" }, text }
        });
        return request;
    }

    private sealed class RejectSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected persistence failure.");
    }

    private sealed class RejectCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected failure after writes, before commit.");
    }

    private sealed class BlockingTeacher : ILessonTeacher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new("Theory", "Question");
        }
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token) => throw new NotSupportedException();
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) => throw new NotSupportedException();
    }
}
