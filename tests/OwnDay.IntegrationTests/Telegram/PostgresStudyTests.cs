using System.Data.Common;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Delivery;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class PostgresStudyTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Dispatch_MigrationRestartsConcurrentWorkersAndFailedDelivery_PreservesOneInvitation()
    {
        await using var database = await Database.CreateAsync();
        var clock = new TelegramStudyTests.Clock { Now = new(2026, 11, 6, 14, 0, 0, TimeSpan.Zero) };
        var sender = new Sender();
        var teacher = new TelegramCurriculumTests.Teacher();
        await using (var seed = database.Context())
        {
            await seed.GetService<IMigrator>().MigrateAsync(Database.Previous);
            var program = LearningProgram.Create(new(101), "Algebra", "Beginner", null, "Mathematics", [new("a", "Algebra", [])], null);
            seed.AddRange(program, LearningContext.Create(program));
            await seed.SaveChangesAsync();
            await seed.Database.MigrateAsync();
            Assert.Empty(await seed.Set<StudySchedule>().ToListAsync());
        }
        using (var factory = new OwnDayHostFactory())
        using (var host = Host(factory, database.Connection, clock, teacher, sender))
        using (var client = host.CreateClient())
        {
            await TelegramStudyTests.Post(client, 1, 101, "/learn_schedule Europe/Moscow | 1 | 18:00-20:00");
            var schedule = await TelegramStudyTests.Dispatch(host.Services);
            Assert.Equal(clock.Now.AddHours(1), schedule.NextAt);
            Assert.Null(schedule.InvitationId);
        }
        clock.Now = clock.Now.AddDays(20).AddHours(1);
        Guid invitation;
        using (var factory = new OwnDayHostFactory())
        using (var host = Host(factory, database.Connection, clock, teacher, sender))
        {
            _ = host.CreateClient();
            var results = await Task.WhenAll(TelegramStudyTests.Dispatch(host.Services), TelegramStudyTests.Dispatch(host.Services));
            invitation = results[0].InvitationId!.Value;
            Assert.Equal(invitation, results[1].InvitationId);
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            Assert.Equal(1, await db.TelegramOutboxMessages.CountAsync(m => m.StudyInvitationId != null));
            using var otherDelivery = host.Services.CreateScope();
            await Task.WhenAll(scope.ServiceProvider.GetRequiredService<TelegramOutboxDeliveryService>().DeliverBatchAsync(CancellationToken.None),
                otherDelivery.ServiceProvider.GetRequiredService<TelegramOutboxDeliveryService>().DeliverBatchAsync(CancellationToken.None));
            var pending = await db.TelegramOutboxMessages.SingleAsync(m => m.StudyInvitationId == invitation);
            Assert.Equal(OutboxMessageStatus.Pending, pending.Status);
            Assert.Equal(1, pending.AttemptCount);
        }
        clock.Now = clock.Now.AddSeconds(3);
        using (var factory = new OwnDayHostFactory())
        using (var host = Host(factory, database.Connection, clock, teacher, sender))
        using (var client = host.CreateClient())
        {
            await TelegramStudyTests.Dispatch(host.Services);
            using (var scope = host.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<TelegramOutboxDeliveryService>().DeliverBatchAsync(CancellationToken.None);
                var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
                var delivered = await db.TelegramOutboxMessages.SingleAsync(m => m.StudyInvitationId == invitation);
                Assert.Equal(OutboxMessageStatus.Sent, delivered.Status);
                Assert.Equal(2, sender.InvitationAttempts);
                Assert.Equal(1, sender.InvitationDeliveries);
                Assert.Equal(clock.Now.AddDays(1).AddSeconds(-3), (await db.Set<StudySchedule>().SingleAsync()).NextAt);
            }
            await TelegramStudyTests.Post(client, 2, 101, "/learn_schedule UTC | 1 | 18:00-20:00");
            await TelegramStudyTests.Post(client, 3, 101, $"/learn_invite_start {invitation:N}");
            await using (var db = database.Context())
            {
                Assert.Equal(new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero), (await db.Set<StudySchedule>().SingleAsync()).NextAt);
                Assert.Empty(await db.Set<Lesson>().ToListAsync());
            }
            clock.Now = new(2026, 12, 10, 21, 0, 0, TimeSpan.Zero);
            var delayed = await TelegramStudyTests.Dispatch(host.Services);
            Assert.Null(delayed.InvitationId);
            Assert.Equal(new DateTimeOffset(2026, 12, 11, 18, 0, 0, TimeSpan.Zero), delayed.NextAt);
            clock.Now = delayed.NextAt;
            var next = await TelegramStudyTests.Dispatch(host.Services);
            Assert.NotNull(next.InvitationId);
            await TelegramStudyTests.Post(client, 4, 101, "/learn_notify_pause");
            using var inspection = host.Services.CreateScope();
            await inspection.ServiceProvider.GetRequiredService<TelegramOutboxDeliveryService>().DeliverBatchAsync(CancellationToken.None);
            var stored = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            var suppressed = await stored.TelegramOutboxMessages.SingleAsync(m => m.StudyInvitationId == next.InvitationId);
            Assert.Equal(OutboxMessageStatus.Failed, suppressed.Status);
            Assert.Equal("StudyInvitationSuppressed", suppressed.LastError);
            Assert.Equal(1, sender.InvitationDeliveries);
            Assert.Empty(await stored.Set<LearningEvidence>().ToListAsync());
            Assert.Empty(await stored.Actions.ToListAsync());
        }
    }

    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Dispatch_OutboxWriteFailure_RollsBackScheduleAndRetriesAfterRestart()
    {
        await using var database = await Database.CreateAsync();
        var clock = new TelegramStudyTests.Clock();
        var failure = new RejectInvitationInsert();
        using (var factory = new OwnDayHostFactory())
        using (var host = Host(factory, database.Connection, clock, new(), new(), failure))
        using (var client = host.CreateClient())
        {
            await TelegramStudyTests.Post(client, 1, 101, "/learn_program Algebra | Beginner");
            await TelegramStudyTests.Post(client, 2, 101, "/learn_schedule Europe/Moscow | 1 | 18:00-20:00");
            failure.Enabled = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => TelegramStudyTests.Dispatch(host.Services));
        }
        await using (var db = database.Context())
        {
            var schedule = await db.Set<StudySchedule>().SingleAsync();
            Assert.Equal(clock.Now, schedule.NextAt);
            Assert.Null(schedule.InvitationId);
            Assert.Null(schedule.LastInvitedAt);
            Assert.Equal(0, await db.TelegramOutboxMessages.CountAsync(m => m.StudyInvitationId != null));
            Assert.Equal(2, await db.ProcessedTelegramUpdates.CountAsync());
        }
        using (var factory = new OwnDayHostFactory())
        using (var host = Host(factory, database.Connection, clock, new(), new()))
        {
            _ = host.CreateClient();
            Assert.NotNull((await TelegramStudyTests.Dispatch(host.Services)).InvitationId);
        }
        await using (var db = database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync(Database.Previous);
            // Query the old schema without the new marker column.
            Assert.Equal(1, await db.TelegramOutboxMessages.CountAsync(m => m.LastError == "StudyScheduleRemoved" && m.Status == OutboxMessageStatus.Failed));
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Set<StudySchedule>().ToListAsync());
            Assert.Equal(1, await db.Set<LearningProgram>().CountAsync());
            Assert.Equal(5, await db.TelegramOutboxMessages.CountAsync());
        }
    }

    [Theory]
    [InlineData("window")]
    [InlineData("settings")]
    [InlineData("lesson")]
    [InlineData("review")]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Deliver_DelayedOrObsoleteInvitation_SuppressesSend(string reason)
    {
        await using var database = await Database.CreateAsync();
        var clock = new TelegramStudyTests.Clock();
        var sender = new Sender { FailFirst = false };
        using var factory = new OwnDayHostFactory();
        using var host = Host(factory, database.Connection, clock, new(), sender);
        using var client = host.CreateClient();
        if (reason == "review")
        {
            await TelegramStudyTests.Post(client, 10, 101, "/learn Algebra");
            await TelegramStudyTests.Post(client, 11, 101, "/answer correct");
            clock.Now = clock.Now.AddDays(1);
        }
        await TelegramStudyTests.Post(client, 1, 101, "/learn_program Algebra | Beginner");
        await TelegramStudyTests.Post(client, 2, 101, "/learn_schedule Europe/Moscow | 1 | 18:00-20:00");
        var invitation = await TelegramStudyTests.Dispatch(host.Services);
        switch (reason)
        {
            case "window": clock.Now = clock.Now.AddHours(3); break;
            case "settings": await TelegramStudyTests.Post(client, 3, 101, "/learn_schedule UTC | 1 | 18:00-20:00"); break;
            case "lesson": await TelegramStudyTests.Post(client, 3, 101, "/learn_next"); break;
            case "review": await TelegramStudyTests.Post(client, 3, 101, $"/learn_review_skip {invitation.SkillId:N}"); break;
        }
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TelegramOutboxDeliveryService>().DeliverBatchAsync(CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var outbox = await db.TelegramOutboxMessages.SingleAsync(m => m.StudyInvitationId == invitation.InvitationId);
        Assert.Equal(OutboxMessageStatus.Failed, outbox.Status);
        Assert.Equal(0, sender.InvitationAttempts);
    }

    private static WebApplicationFactory<Program> Host(OwnDayHostFactory factory, string connection,
        TelegramStudyTests.Clock clock, TelegramCurriculumTests.Teacher teacher, Sender sender,
        RejectInvitationInsert? failure = null) => factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OwnDayDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<OwnDayDbContext>>();
            services.AddDbContext<OwnDayDbContext>(options =>
            {
                options.UseNpgsql(connection);
                if (failure is not null) options.AddInterceptors(failure);
            });
            TelegramStudyTests.Configure(services, clock, teacher);
            services.Replace(ServiceDescriptor.Singleton<ITelegramMessageSender>(sender));
        }));

    private sealed class Sender : ITelegramMessageSender
    {
        public bool FailFirst { get; init; } = true;
        public int InvitationAttempts { get; private set; }
        public int InvitationDeliveries { get; private set; }
        public Task SendMessageAsync(long chatId, string text, string? replyMarkupJson, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!text.StartsWith("Time to study:", StringComparison.Ordinal)) return Task.CompletedTask;
            InvitationAttempts++;
            if (FailFirst && InvitationAttempts == 1) throw new HttpRequestException("Temporary failure");
            InvitationDeliveries++;
            return Task.CompletedTask;
        }
    }

    private sealed class RejectInvitationInsert : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("INSERT INTO telegram_outbox_messages", StringComparison.Ordinal))
                throw new InvalidOperationException("Injected outbox write failure.");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class Database(string connection, string schema, OwnDayDbContext admin) : IAsyncDisposable
    {
        public const string Previous = "20261106143915_AddMultipleLearningPrograms";
        public string Connection => connection;
        public OwnDayDbContext Context() => new(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connection).Options);
        public static async Task<Database> CreateAsync()
        {
            var connection = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
                throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required.");
            var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
            var admin = new OwnDayDbContext(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connection).Options);
#pragma warning disable EF1003 // Schema name is generated from a GUID.
            await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003
            var database = new Database(connection + ";Search Path=" + schema, schema, admin);
            try
            {
                await using var db = database.Context();
                await db.Database.MigrateAsync();
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
#pragma warning disable EF1003 // Same GUID-derived schema name.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
            await admin.DisposeAsync();
        }
    }
}
