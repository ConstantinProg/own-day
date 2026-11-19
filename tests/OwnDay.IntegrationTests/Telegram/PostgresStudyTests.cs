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
        var clock = new TelegramStudyFixture.Clock { Now = new(2026, 11, 6, 14, 0, 0, TimeSpan.Zero) };
        var sender = new Sender();
        var teacher = new TelegramCurriculumFixture.Teacher();
        await using (var seed = database.Context())
        {
            await seed.GetService<IMigrator>().MigrateAsync(Database.Previous);
            var program = LearningProgram.Create(new(101), "Algebra", "Beginner", null, "Mathematics", [new("a", "Algebra", [])], null);
            var topics = System.Text.Json.JsonSerializer.Serialize(program.CurrentRoute.Topics);
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO learning_programs ("Id", "UserId", "Goal", "Experience", "Discipline", "DiagnosticPending", "Priority")
                VALUES ({program.Id}, 101, {program.Goal}, {program.Experience}, {program.Discipline}, false, 1);
                INSERT INTO learning_routes ("Id", "ProgramId", "Version", "Reason", "Topics")
                VALUES ({program.CurrentRoute.Id}, {program.Id}, 1, {program.CurrentRoute.Reason}, CAST({topics} AS jsonb));
                INSERT INTO learning_contexts ("UserId", "ProgramId") VALUES (101, {program.Id});
                """);
            await seed.Database.MigrateAsync();
            Assert.Empty(await seed.Set<StudySchedule>().ToListAsync());
        }
        using (var factory = new OwnDayHostFactory())
        using (var host = Host(factory, database.Connection, clock, teacher, sender))
        using (var client = host.CreateClient())
        {
            await LearningWorkerFixture.ProcessAsync(host.Services, 1, 101, "/learn_schedule Europe/Moscow | 1 | 18:00-20:00");
            var schedule = await TelegramStudyFixture.Dispatch(host.Services);
            Assert.Equal(clock.Now.AddHours(1), schedule.NextAt);
            Assert.Null(schedule.InvitationId);
        }
        clock.Now = clock.Now.AddDays(20).AddHours(1);
        Guid invitation;
        using (var factory = new OwnDayHostFactory())
        using (var host = Host(factory, database.Connection, clock, teacher, sender))
        {
            _ = host.CreateClient();
            var results = await Task.WhenAll(TelegramStudyFixture.Dispatch(host.Services), TelegramStudyFixture.Dispatch(host.Services));
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
            await TelegramStudyFixture.Dispatch(host.Services);
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
            await LearningWorkerFixture.ProcessAsync(host.Services, 2, 101, "/learn_schedule UTC | 1 | 18:00-20:00");
            await LearningWorkerFixture.ProcessAsync(host.Services, 3, 101, $"/learn_invite_start {invitation:N}");
            await using (var db = database.Context())
            {
                Assert.Equal(new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero), (await db.Set<StudySchedule>().SingleAsync()).NextAt);
                Assert.Empty(await db.Set<Lesson>().ToListAsync());
            }
            clock.Now = new(2026, 12, 10, 21, 0, 0, TimeSpan.Zero);
            var delayed = await TelegramStudyFixture.Dispatch(host.Services);
            Assert.Null(delayed.InvitationId);
            Assert.Equal(new DateTimeOffset(2026, 12, 11, 18, 0, 0, TimeSpan.Zero), delayed.NextAt);
            clock.Now = delayed.NextAt;
            var next = await TelegramStudyFixture.Dispatch(host.Services);
            Assert.NotNull(next.InvitationId);
            await LearningWorkerFixture.ProcessAsync(host.Services, 4, 101, "/learn_notify_pause");
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
        var clock = new TelegramStudyFixture.Clock();
        var failure = new RejectInvitationInsert();
        using (var factory = new OwnDayHostFactory())
        using (var host = Host(factory, database.Connection, clock, new(), new(), failure))
        using (var client = host.CreateClient())
        {
            await LearningWorkerFixture.ProcessAsync(host.Services, 1, 101, "/learn_program Algebra | Beginner");
            await LearningWorkerFixture.ProcessAsync(host.Services, 2, 101, "/learn_schedule Europe/Moscow | 1 | 18:00-20:00");
            failure.Enabled = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => TelegramStudyFixture.Dispatch(host.Services));
        }
        await using (var db = database.Context())
        {
            var schedule = await db.Set<StudySchedule>().SingleAsync();
            Assert.Equal(clock.Now, schedule.NextAt);
            Assert.Null(schedule.InvitationId);
            Assert.Null(schedule.LastInvitedAt);
            Assert.Equal(0, await db.TelegramOutboxMessages.CountAsync(m => m.StudyInvitationId != null));
            Assert.Equal(2, await db.Set<OwnDay.Application.Learning.Requests.LearningRequest>().CountAsync());
        }
        using (var factory = new OwnDayHostFactory())
        using (var host = Host(factory, database.Connection, clock, new(), new()))
        {
            _ = host.CreateClient();
            Assert.NotNull((await TelegramStudyFixture.Dispatch(host.Services)).InvitationId);
        }
        await using (var db = database.Context())
        {
            var rejected = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(Database.Previous));
            Assert.Contains("Cannot remove shared ledger", rejected.MessageText, StringComparison.Ordinal);
            await db.Database.MigrateAsync();
            Assert.NotNull((await db.Set<StudySchedule>().SingleAsync()).InvitationId);
            Assert.Equal(1, await db.Set<LearningProgram>().CountAsync());
            Assert.Equal(5, await db.TelegramOutboxMessages.CountAsync());
        }
    }

    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Migrate_InvitationWithoutLearningHistory_RetiresPendingDeliveryWithoutLosingProgram()
    {
        await using var database = await Database.CreateAsync();
        await using var db = database.Context();
        var now = new DateTimeOffset(2026, 11, 6, 18, 0, 0, TimeSpan.Zero);
        var program = LearningProgram.Create(new(101), "Algebra", "Beginner", null, "Mathematics", [new("a", "Algebra", [])], null);
        var schedule = StudySchedule.Create(new(101), "UTC", 1, [new(new TimeOnly(18, 0), new TimeOnly(20, 0))], now);
        var invitation = schedule.Invite(program.Id, null, now);
        db.AddRange(program, schedule);
        db.TelegramOutboxMessages.Add(new TelegramOutboxMessage
        {
            Id = Guid.NewGuid(), ChatId = 101, Text = "Pending invitation",
            Status = OutboxMessageStatus.Pending, CreatedAt = now.UtcDateTime,
            NextAttemptAt = now.UtcDateTime, StudyInvitationId = invitation
        });
        await db.SaveChangesAsync();

        await db.GetService<IMigrator>().MigrateAsync(Database.Previous);

        Assert.Equal(1, await db.TelegramOutboxMessages.CountAsync(message => message.LastError == "StudyScheduleRemoved" && message.Status == OutboxMessageStatus.Failed));
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Set<StudySchedule>().ToListAsync());
        Assert.Single(await db.Set<LearningProgram>().ToListAsync());
        Assert.Single(await db.TelegramOutboxMessages.ToListAsync());
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
        var clock = new TelegramStudyFixture.Clock();
        var sender = new Sender { FailFirst = false };
        using var factory = new OwnDayHostFactory();
        using var host = Host(factory, database.Connection, clock, new(), sender);
        using var client = host.CreateClient();
        if (reason == "review")
        {
            await LearningWorkerFixture.ProcessAsync(host.Services, 10, 101, "/learn Algebra");
            await LearningWorkerFixture.ProcessAsync(host.Services, 11, 101, "/answer correct");
            clock.Now = clock.Now.AddDays(1);
        }
        await LearningWorkerFixture.ProcessAsync(host.Services, 1, 101, "/learn_program Algebra | Beginner");
        await LearningWorkerFixture.ProcessAsync(host.Services, 2, 101, "/learn_schedule Europe/Moscow | 1 | 18:00-20:00");
        var invitation = await TelegramStudyFixture.Dispatch(host.Services);
        switch (reason)
        {
            case "window": clock.Now = clock.Now.AddHours(3); break;
            case "settings": await LearningWorkerFixture.ProcessAsync(host.Services, 3, 101, "/learn_schedule UTC | 1 | 18:00-20:00"); break;
            case "lesson": await LearningWorkerFixture.ProcessAsync(host.Services, 3, 101, "/learn_next"); break;
            case "review": await LearningWorkerFixture.ProcessAsync(host.Services, 3, 101, $"/learn_review_skip {invitation.SkillId:N}"); break;
        }
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TelegramOutboxDeliveryService>().DeliverBatchAsync(CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var outbox = await db.TelegramOutboxMessages.SingleAsync(m => m.StudyInvitationId == invitation.InvitationId);
        Assert.Equal(OutboxMessageStatus.Failed, outbox.Status);
        Assert.Equal(0, sender.InvitationAttempts);
    }

    [Theory]
    [InlineData("allowed")]
    [InlineData("off")]
    [InlineData("answer")]
    [InlineData("new prompt")]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Deliver_OpenExerciseReminder_RevalidatesConsentAndOriginalQuestion(string variant)
    {
        await using var database = await Database.CreateAsync();
        var clock = new TelegramStudyFixture.Clock();
        var sender = new Sender { FailFirst = false };
        using (var factory = new OwnDayHostFactory())
        using (var host = Host(factory, database.Connection, clock, new(), sender))
        using (var client = host.CreateClient())
        {
            await LearningWorkerFixture.ProcessAsync(host.Services, 1, 101, "/learn_program Algebra | Beginner");
            using (var scope = host.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<StudyInvitationDispatcher>().DispatchBatchAsync(default);
            await LearningWorkerFixture.ProcessAsync(host.Services, 2, 101, "/learn_schedule Europe/Moscow | 1 | 18:00-20:00 | review");
            Assert.Null((await TelegramStudyFixture.Dispatch(host.Services)).InvitationId);
            await LearningWorkerFixture.ProcessAsync(host.Services, 3, 101, "/learn_next");
            if (variant == "new prompt")
            {
                await using var db = database.Context();
                var lesson = await db.Set<Lesson>().SingleAsync();
                lesson.SetContent(new("algebra", "rubric-v1", LearningContentKind.StructuredExercise, "Explain algebra",
                    "Identify the rule", "Explain the rule in the calculation", new(1, ExerciseEvidenceKind.Understanding,
                        [new("rule", "Identify the applicable rule")])));
                await db.SaveChangesAsync();
            }
            await LearningWorkerFixture.ProcessAsync(host.Services, 4, 101, "/learn_schedule Europe/Moscow | 1 | 18:00-20:00 | open");
            var planned = await Task.WhenAll(TelegramStudyFixture.Dispatch(host.Services), TelegramStudyFixture.Dispatch(host.Services));
            Assert.Equal(planned[0].InvitationId, planned[1].InvitationId);
            Assert.NotNull(planned[0].LessonId);
            if (variant == "off")
            {
                await LearningWorkerFixture.ProcessAsync(host.Services, 5, 101, "/learn_notify_off");
                await LearningWorkerFixture.ProcessAsync(host.Services, 6, 101, "/learn_notify_resume");
            }
            else if (variant == "answer") await LearningWorkerFixture.ProcessAsync(host.Services, 5, 101, "/answer correct");
            else if (variant == "new prompt")
            {
                await using var db = database.Context();
                var lesson = await db.Set<Lesson>().SingleAsync();
                lesson.ApplyRubricAssessment("A partial answer", "A detail is missing", new([new("rule", RubricCriterionResult.Unclear,
                    "Please identify the rule")]), Guid.NewGuid());
                await db.SaveChangesAsync();
            }
        }

        using var restartedFactory = new OwnDayHostFactory();
        using var restarted = Host(restartedFactory, database.Connection, clock, new(), sender);
        using var observer = restarted.CreateClient();
        using var inspection = restarted.Services.CreateScope();
        var delivery = inspection.ServiceProvider.GetRequiredService<TelegramOutboxDeliveryService>();
        while ((await delivery.DeliverBatchAsync(default)).IsFullBatch) { }
        var result = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var outbox = await result.TelegramOutboxMessages.SingleAsync(message => message.StudyInvitationId != null);
        Assert.Equal(variant == "allowed" ? OutboxMessageStatus.Sent : OutboxMessageStatus.Failed, outbox.Status);
        Assert.Equal(variant == "allowed" ? 1 : 0, sender.InvitationDeliveries);
        Assert.Equal(1, await result.Set<Lesson>().CountAsync());
        Assert.False(result.Database.HasPendingModelChanges());
        if (variant == "off") Assert.Equal(StudyReminderType.None, (await result.Set<StudySchedule>().SingleAsync()).EffectiveReminderTypes);
    }

    private static WebApplicationFactory<Program> Host(OwnDayHostFactory factory, string connection,
        TelegramStudyFixture.Clock clock, TelegramCurriculumFixture.Teacher teacher, Sender sender,
        RejectInvitationInsert? failure = null) => factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OwnDayDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<OwnDayDbContext>>();
            services.AddDbContext<OwnDayDbContext>(options =>
            {
                options.UseNpgsql(connection);
                if (failure is not null) options.AddInterceptors(failure);
            });
            TelegramStudyFixture.Configure(services, clock, teacher);
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
            if (!text.StartsWith("Time to study:", StringComparison.Ordinal) &&
                !text.StartsWith("Есть открытое упражнение", StringComparison.Ordinal)) return Task.CompletedTask;
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
