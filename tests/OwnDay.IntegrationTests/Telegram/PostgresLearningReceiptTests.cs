using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

[Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
public sealed class PostgresLearningReceiptTests : IAsyncLifetime
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
    public async Task Post_ConcurrentDuplicate_CommitsOneEffectAndOneReceipt()
    {
        var teacher = new TelegramCurriculumTests.Teacher();
        using var first = Host(teacher);
        using var second = Host(teacher);
        using var firstClient = first.CreateClient();
        using var secondClient = second.CreateClient();
        await Task.WhenAll(
            TelegramCurriculumTests.PostAsync(firstClient, 1, 101, "/learn algebra"),
            TelegramCurriculumTests.PostAsync(secondClient, 1, 101, "/learn algebra"));

        await using var db = Open();
        Assert.Single(await db.Set<Lesson>().ToListAsync());
        Assert.Equal(1, teacher.Generated);
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS \"Value\" FROM learning_requests").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("""
            SELECT count(*) AS "Value" FROM telegram_outbox_messages WHERE learning_request_id IS NULL
            """).SingleAsync());
    }

    [Fact]
    public async Task Post_ConcurrentCommandsForOneOwner_AssignsDistinctContiguousSequences()
    {
        var teacher = new TelegramCurriculumTests.Teacher();
        using var first = Host(teacher);
        using var second = Host(teacher);
        using var firstClient = first.CreateClient();
        using var secondClient = second.CreateClient();
        await Task.WhenAll(
            TelegramCurriculumTests.PostAsync(firstClient, 1, 101, "/learn algebra"),
            TelegramCurriculumTests.PostAsync(secondClient, 2, 101, "/learn geometry"));

        await using var db = Open();
        Assert.Equal(new long[] { 1, 2 }, await db.Database.SqlQueryRaw<long>("""
            SELECT sequence AS "Value" FROM learning_requests WHERE user_id = 101 ORDER BY sequence
            """).ToArrayAsync());
        Assert.Single(await db.Set<Lesson>().ToListAsync());
        Assert.Equal(1, teacher.Generated);
    }

    [Fact]
    public async Task Post_CommitFailure_RollsBackEffectReceiptAndRepliesBeforeSuccessfulRedelivery()
    {
        using (var failed = Host(new TelegramCurriculumTests.Teacher(), new RejectCommit()))
        using (var client = failed.CreateClient())
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook"))
        {
            request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
            request.Content = JsonContent.Create(new
            {
                update_id = 1,
                message = new { message_id = 1, date = 1700000000, chat = new { id = 101, type = "private" },
                    from = new { id = 101, is_bot = false, first_name = "Learner" }, text = "/learn algebra" }
            });
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        await using var db = Open();
        Assert.Empty(await db.Set<Lesson>().ToListAsync());
        Assert.Empty(await db.ProcessedTelegramUpdates.ToListAsync());
        Assert.Empty(await db.TelegramOutboxMessages.ToListAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS \"Value\" FROM learning_requests").SingleAsync());

        using var recovered = Host(new TelegramCurriculumTests.Teacher());
        using var recoveredClient = recovered.CreateClient();
        await TelegramCurriculumTests.PostAsync(recoveredClient, 1, 101, "/learn algebra");
        Assert.Single(await db.Set<Lesson>().ToListAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("""
            SELECT count(*) AS "Value" FROM learning_requests
            WHERE status = 0 AND outcome = 'reply_committed' AND completed_at >= received_at
            """).SingleAsync());
        Assert.All(await db.TelegramOutboxMessages.ToListAsync(), message =>
        {
            Assert.NotNull(message.LearningRequestId);
            Assert.NotNull(message.ReplyOrdinal);
        });
    }

    private OwnDayDbContext Open() => new(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(_connection).Options);

    private WebApplicationFactory<Program> Host(ILessonTeacher teacher, IInterceptor? interceptor = null) =>
        new OwnDayHostFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
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

    private sealed class RejectCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected commit failure after all writes.");
    }
}
