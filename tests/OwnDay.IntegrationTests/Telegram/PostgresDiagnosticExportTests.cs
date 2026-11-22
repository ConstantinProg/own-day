using System.IO.Compression;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using OwnDay.Application.Learning.Requests;
using OwnDay.Application.Learning;
using OwnDay.Application.TesterMode;
using OwnDay.Infrastructure.Diagnostics;
using OwnDay.Infrastructure.Learning;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Telegram.Delivery;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresTesterModeTests
{
    [Fact]
    public async Task Post_ExportRequiresPrivateAuthorizedTester_DoesNotQueueUnauthorizedOrGroupRequests()
    {
        using var host = Host();
        using var client = host.CreateClient();
        await PostAsync(client, 301, "/export");
        await AuthorizeAsync(client);
        using var group = System.Net.Http.Json.JsonContent.Create(new
        {
            update_id = 302,
            message = new { message_id = 302, date = 1790244000, from = new { id = Tester.Value, is_bot = false, first_name = "Tester" },
                chat = new { id = -99999, type = "supergroup" }, text = "/export recent" }
        });
        await TesterModeAuthorizationTests.PostUpdateAsync(client, group, System.Net.HttpStatusCode.OK);
        using var disabled = host.WithWebHostBuilder(builder => builder.UseSetting("TesterMode:Enabled", "false"));
        using var disabledClient = disabled.CreateClient();
        await PostAsync(disabledClient, 303, "/export last");

        await using var db = Open();
        Assert.Empty(await db.Set<DiagnosticExportJob>().ToListAsync());
        Assert.Empty(await db.Set<DiagnosticDocument>().ToListAsync());
        Assert.Equal(2, await db.TelegramOutboxMessages.CountAsync(row => row.Text == "Export requires active Tester Mode."));
    }

    [Fact]
    public async Task BuildAsync_AllModes_UsesOwnerSnapshotAndStableWindowAndSelectsNewestReceipt()
    {
        using var host = Host();
        using var client = host.CreateClient();
        await AuthorizeAsync(client);
        var now = DateTime.UtcNow;
        var latest = new LearningRequest
        {
            UserId = Tester, ChatId = Tester.Value, Sequence = 2, SourceKey = "test:latest", SourceUpdateId = 402,
            NormalizedInput = "{}", Status = LearningRequestStatus.Ready, Outcome = null, ReceivedAt = now.AddMinutes(-2),
            AvailableAt = now.AddMinutes(-2)
        };
        var completed = new LearningRequest
        {
            UserId = Tester, ChatId = Tester.Value, Sequence = 1, SourceKey = "test:completed", SourceUpdateId = 401,
            NormalizedInput = "{}", Status = LearningRequestStatus.Completed, ReceivedAt = now.AddMinutes(-3),
            AvailableAt = now.AddMinutes(-3), CompletedAt = now.AddMinutes(-1)
        };
        var ids = Enumerable.Range(1, 103).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();
        await using (var db = Open())
        {
            SeedLearning(db, Tester, "My private goal");
            SeedLearning(db, Other, "Someone else's private goal");
            db.AddRange(latest, completed);
            db.AddRange(ids.Select(id => Event(id, Tester.Value, now.AddMinutes(-1), latest.Id)));
            db.AddRange(Event(Guid.NewGuid(), Tester.Value, now.AddHours(-25), latest.Id),
                Event(Guid.NewGuid(), Tester.Value, now.AddDays(-31), latest.Id),
                Event(Guid.NewGuid(), Other.Value, now.AddMinutes(-1), latest.Id, "other_owner_secret"));
            await db.SaveChangesAsync();
        }

        await using var scope = host.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<DiagnosticExportReader>();
        var recent = await reader.BuildAsync(Job(DiagnosticExportMode.Recent), default);
        using (var json = JsonDocument.Parse(recent.Bytes!))
        {
            var rows = json.RootElement.GetProperty("timeline").EnumerateArray().ToArray();
            Assert.Equal(100, rows.Length);
            Assert.Equal(ids.Skip(3), rows.Select(row => row.GetProperty("id").GetGuid()));
            Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("currentState").GetProperty("requests")
                .GetProperty("pendingRequests").GetInt32());
        }
        var last = await reader.BuildAsync(Job(DiagnosticExportMode.Last), default);
        using (var json = JsonDocument.Parse(last.Bytes!))
        {
            Assert.Equal(latest.Id, json.RootElement.GetProperty("metadata").GetProperty("requestId").GetGuid());
            Assert.Equal("ready", Assert.Single(json.RootElement.GetProperty("snapshot").GetProperty("requests").EnumerateArray()).GetProperty("status").GetString());
            Assert.Equal(104, json.RootElement.GetProperty("timeline").GetArrayLength());
        }
        var full = await reader.BuildAsync(Job(DiagnosticExportMode.Full), default);
        using var archive = new ZipArchive(new MemoryStream(full.Bytes!));
        var file = Assert.Single(archive.Entries);
        Assert.Equal("diagnostics.json", file.FullName);
        using var stream = file.Open();
        using var document = await JsonDocument.ParseAsync(stream);
        Assert.Equal(104, document.RootElement.GetProperty("timeline").GetArrayLength());
        Assert.All(document.RootElement.GetProperty("completeness").GetProperty("sections").EnumerateObject(),
            section => Assert.Equal("complete", section.Value.GetProperty("status").GetString()));
        var serialized = document.RootElement.GetRawText();
        Assert.Contains("My private goal", serialized);
        Assert.DoesNotContain("Someone else", serialized);
        Assert.DoesNotContain("other_owner_secret", serialized);
        Assert.DoesNotContain("integration-test-webhook-secret", serialized);
    }

    [Fact]
    public async Task Post_DuplicateDelivery_QueuesOnceAndDocumentSurvivesWorkerRestartAndSendRetry()
    {
        using var host = Host();
        using var client = host.CreateClient();
        await AuthorizeAsync(client);
        await PostAsync(client, 501, "/export");
        await PostAsync(client, 501, "/export");
        await using (var before = Open())
            Assert.Single(await before.Set<DiagnosticExportJob>().ToListAsync());
        using (var worker = ActivatorUtilities.CreateInstance<DiagnosticExportWorker>(host.Services))
        {
            await worker.StartAsync(default);
            await EventuallyAsync(async () =>
            {
                await using var db = Open();
                return await db.Set<DiagnosticDocument>().AnyAsync();
            });
            await worker.StopAsync(default);
        }
        using var restarted = Host();
        using var restartedClient = restarted.CreateClient();
        var sender = new ExportSender();
        await using (var attempt = restarted.Services.CreateAsyncScope())
        {
            var db = attempt.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            var delivery = ActivatorUtilities.CreateInstance<TelegramOutboxDeliveryService>(attempt.ServiceProvider, sender);
            await delivery.DeliverBatchAsync(default);
            var pending = await db.TelegramOutboxMessages.SingleAsync(row => row.DocumentName != null);
            Assert.Equal(1, pending.AttemptCount);
            Assert.True(await db.Set<DiagnosticDocument>().AnyAsync());
            pending.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        await using (var attempt = restarted.Services.CreateAsyncScope())
        {
            var delivery = ActivatorUtilities.CreateInstance<TelegramOutboxDeliveryService>(attempt.ServiceProvider, sender);
            await delivery.DeliverBatchAsync(default);
        }
        Assert.Equal(2, sender.Calls);
        Assert.Equal(Tester.Value, sender.ChatId);
        await using var after = Open();
        Assert.Empty(await after.Set<DiagnosticDocument>().ToListAsync());
        Assert.Equal(OutboxMessageStatus.Sent, (await after.TelegramOutboxMessages.SingleAsync(row => row.DocumentName != null)).Status);
    }

    [Fact]
    public async Task Post_ResetFencesBufferedEvents_AndOffStopsDetailedCapture()
    {
        using var host = Host();
        using var client = host.CreateClient();
        await AuthorizeAsync(client);
        var journal = host.Services.GetRequiredService<DiagnosticJournal>();
        var correlation = Guid.NewGuid();
        var stale = await journal.CaptureAsync(Tester, correlation, null, null, default);
        Assert.NotNull(stale);
        Assert.True(stale.Detailed);
        using (DiagnosticJournal.Enter(stale))
            journal.Write("learning", "stale_event");
        await using (var seed = Open())
        {
            seed.Add(Event(Guid.NewGuid(), Tester.Value, DateTime.UtcNow, correlation));
            await seed.SaveChangesAsync();
        }
        await PostAsync(client, 601, "/tester_reset");
        await CallbackAsync(client, 602, (await ReadButtonsAsync())[0]);
        await PostAsync(client, 603, "/tester_off");
        var fresh = await journal.CaptureAsync(Tester, correlation, null, null, default);
        Assert.NotNull(fresh);
        Assert.False(fresh.Detailed);
        Assert.NotEqual(stale.Generation, fresh.Generation);
        using (DiagnosticJournal.Enter(fresh))
            journal.Write("learning", "fresh_event", detail: () => new { text = "must not be recorded" });
        using var worker = ActivatorUtilities.CreateInstance<DiagnosticWriter>(host.Services);
        await worker.StartAsync(default);
        await EventuallyAsync(async () =>
        {
            await using var db = Open();
            return await db.Set<DiagnosticEvent>().AnyAsync(row => row.Code == "fresh_event");
        });
        await worker.StopAsync(default);
        await using var after = Open();
        var row = Assert.Single(await after.Set<DiagnosticEvent>().ToListAsync());
        Assert.Equal("fresh_event", row.Code);
        Assert.Equal("minimal", row.Detail);
        Assert.DoesNotContain("must not be recorded", row.Data);
    }

    [Fact]
    public async Task BuildAsync_OneSectionCannotBeRead_ReportsUnavailableAndPreservesOtherSections()
    {
        using var host = Host();
        using var client = host.CreateClient();
        await AuthorizeAsync(client);
        await using (var db = Open())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE learning_reviews RENAME TO unavailable_learning_reviews");
        await using var scope = host.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<DiagnosticExportReader>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.BuildAsync(Job(DiagnosticExportMode.Recent), cancelled.Token));
        var result = await reader.BuildAsync(Job(DiagnosticExportMode.Recent), default);
        using var json = JsonDocument.Parse(result.Bytes!);
        var root = json.RootElement;
        var sections = root.GetProperty("completeness").GetProperty("sections");
        Assert.Equal("unavailable", sections.GetProperty("reviewSchedules").GetProperty("status").GetString());
        Assert.Equal("section_read_failed", sections.GetProperty("reviewSchedules").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("snapshot").GetProperty("knowledge").GetProperty("reviewSchedules").ValueKind);
        Assert.Equal("complete", sections.GetProperty("requests").GetProperty("status").GetString());
    }

    [Fact]
    public async Task BuildAsync_CommitDuringSnapshot_UsesOnePostgresSnapshot()
    {
        using var host = Host();
        using var client = host.CreateClient();
        await AuthorizeAsync(client);
        var concurrent = new ReadInterleaving(async token =>
        {
            await using var writer = Open();
            SeedLearning(writer, Tester, "committed after first snapshot read");
            await writer.SaveChangesAsync(token);
        });
        await using var db = new OwnDayDbContext(new DbContextOptionsBuilder<OwnDayDbContext>()
            .UseNpgsql(_connection).AddInterceptors(concurrent).Options);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var curriculum = new EfCurriculumStore(db);
        var tester = new TesterModeService(new EfTesterModeStore(db, curriculum), services.GetRequiredService<TesterModeOptions>());
        var reader = ActivatorUtilities.CreateInstance<DiagnosticExportReader>(services, db, tester, (ICurriculumStore)curriculum);
        var result = await reader.BuildAsync(Job(DiagnosticExportMode.Recent), default);
        Assert.True(concurrent.Ran);
        Assert.DoesNotContain("committed after first snapshot read", System.Text.Encoding.UTF8.GetString(result.Bytes!));
        using var json = JsonDocument.Parse(result.Bytes!);
        Assert.Equal("complete", json.RootElement.GetProperty("completeness").GetProperty("sections").GetProperty("programs")
            .GetProperty("status").GetString());
        await using var actual = Open();
        Assert.Single(await actual.Set<LearningProgram>().ToListAsync());
    }

    [Fact]
    public async Task ApplyAsync_CommitFails_RecordsUnknownWithoutClaimingSuccessfulChanges()
    {
        using var host = Host();
        using var client = host.CreateClient();
        await AuthorizeAsync(client);
        await PostAsync(client, 701, "Test the transaction");
        await using var setup = Open();
        var lease = await new EfLearningRequestStore(setup).ClaimAsync(TimeSpan.FromMinutes(2), default);
        Assert.NotNull(lease);
        var journal = host.Services.GetRequiredService<DiagnosticJournal>();
        var text = host.Services.GetRequiredService<DiagnosticText>();
        var context = await journal.CaptureAsync(Tester, lease.RequestId, lease.RequestId, lease.Attempt, default);
        Assert.NotNull(context);
        using (DiagnosticJournal.Enter(context))
        await using (var db = new OwnDayDbContext(new DbContextOptionsBuilder<OwnDayDbContext>()
            .UseNpgsql(_connection).AddInterceptors(new FailedCommit()).Options))
        {
            var store = new EfLearningRequestStore(db, journal, text);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyAsync(lease, (_, _) =>
            {
                db.Add(Skill.Create(Tester, "Proposed skill", "Must roll back"));
                return Task.FromResult(new LearningRequestCompletion("test"));
            }, default));
        }
        using var writer = ActivatorUtilities.CreateInstance<DiagnosticWriter>(host.Services);
        await writer.StartAsync(default);
        await EventuallyAsync(async () =>
        {
            await using var db = Open();
            return await db.Set<DiagnosticEvent>().AnyAsync(row => row.Code == "commit_outcome_unknown");
        });
        await writer.StopAsync(default);
        await using var saved = Open();
        Assert.False(await saved.Set<Skill>().AnyAsync());
        Assert.True(await saved.Set<DiagnosticEvent>().AnyAsync(row => row.Code == "state_change_proposed"));
        Assert.False(await saved.Set<DiagnosticEvent>().AnyAsync(row => row.Code == "state_change_committed"));
        Assert.Equal(LearningRequestStatus.Running, (await saved.Set<LearningRequest>().SingleAsync()).Status);
    }

    [Fact]
    public async Task Post_WhileAuthorizingAndRevoking_StoresAllowedTextOnlyAndPrunesExpiredHistory()
    {
        using var host = Host();
        using var client = host.CreateClient();
        await PostAsync(client, 801, "ordinary content");
        await AuthorizeAsync(client);
        await PostAsync(client, 802, "tester content");
        await PostAsync(client, 803, "password: integration-db-secret");
        await using (var seed = Open())
        {
            seed.Add(Event(Guid.NewGuid(), Tester.Value, DateTime.UtcNow.AddDays(-31), Guid.NewGuid(), "expired_event"));
            await seed.SaveChangesAsync();
        }
        using var writer = ActivatorUtilities.CreateInstance<DiagnosticWriter>(host.Services);
        await writer.StartAsync(default);
        await EventuallyAsync(async () =>
        {
            await using var db = Open();
            return await db.Set<DiagnosticEvent>().CountAsync(row => row.Code == "request_received") == 3 &&
                !await db.Set<DiagnosticEvent>().AnyAsync(row => row.Code == "expired_event");
        });
        await PostAsync(client, 804, "/tester_off");
        await PostAsync(client, 805, "after revocation content");
        await EventuallyAsync(async () =>
        {
            await using var db = Open();
            return await db.Set<DiagnosticEvent>().CountAsync(row => row.Code == "request_received") == 4;
        });
        await writer.StopAsync(default);
        await using var saved = Open();
        var payloads = string.Join('\n', await saved.Set<DiagnosticEvent>().Select(row => row.Data).ToArrayAsync());
        Assert.Contains("tester content", payloads);
        Assert.DoesNotContain("ordinary content", payloads);
        Assert.DoesNotContain("integration-db-secret", payloads);
        Assert.DoesNotContain("after revocation content", payloads);
        Assert.DoesNotContain(TesterModeAuthorizationTests.Secret, payloads);
    }

    [Fact]
    public async Task WriteAsync_TransientJournalOutage_RetriesIndependentlyAndReplayDoesNotDuplicate()
    {
        using var host = Host();
        using var client = host.CreateClient();
        var outage = new RejectFirstJournalBatch();
        using var diagnostics = new ServiceCollection()
            .AddDbContext<OwnDayDbContext>(options => options.UseNpgsql(_connection).AddInterceptors(outage))
            .BuildServiceProvider();
        var journal = host.Services.GetRequiredService<DiagnosticJournal>();
        using var worker = new DiagnosticWriter(journal, diagnostics.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DiagnosticOptions()), NullLogger<DiagnosticWriter>.Instance, TimeProvider.System);
        await worker.StartAsync(default);
        await PostAsync(client, 901, "accepted while the journal retries");
        await EventuallyAsync(async () =>
        {
            await using var db = Open();
            return await db.Set<DiagnosticEvent>().AnyAsync(row => row.Code == "request_queued");
        });
        await worker.StopAsync(default);
        await using var saved = Open();
        var request = await saved.Set<LearningRequest>().SingleAsync();
        Assert.Equal(LearningRequestStatus.Ready, request.Status);
        var queued = await saved.Set<DiagnosticEvent>().Where(row => row.Code == "request_queued").ToArrayAsync();
        Assert.Single(queued);
        Assert.True(await worker.WriteAsync(Tester.Value, queued, default));
        Assert.Equal(1, await saved.Set<DiagnosticEvent>().CountAsync(row => row.Code == "request_queued"));
    }

    [Fact]
    public async Task Post_FailedReceiptAndRedelivery_RetainsOneCorrelationAcrossAttempts()
    {
        using var host = Host();
        using var client = host.CreateClient();
        var processing = host.Services.GetRequiredService<IOptions<LearningRequestProcessingOptions>>().Value;
        processing.AcceptancePaused = true;
        await TesterModeAuthorizationTests.PostAsync(client, 902, "Retry the same Telegram update",
            status: System.Net.HttpStatusCode.InternalServerError);
        processing.AcceptancePaused = false;
        await PostAsync(client, 902, "Retry the same Telegram update");

        await using var db = Open();
        var request = await db.Set<LearningRequest>().SingleAsync();
        var reader = host.Services.GetRequiredService<DiagnosticJournal>().Reader;
        var events = new List<DiagnosticEvent>();
        while (reader.TryRead(out var row)) events.Add(row);
        Assert.Single(events, row => row.Code == "ingress_failed");
        Assert.Single(events, row => row.Code == "request_queued");
        Assert.Equal(2, events.Count(row => row.Code == "request_received"));
        Assert.All(events, row => Assert.Equal(request.Id, row.CorrelationId));
    }

    private static DiagnosticExportJob Job(DiagnosticExportMode mode) =>
        new() { Mode = mode, OwnerId = Tester.Value, ReceivedAt = DateTime.UtcNow };

    private static DiagnosticEvent Event(Guid id, long owner, DateTime occurredAt, Guid request, string code = "test") =>
        new(id, owner, Guid.Empty, occurredAt, "learning", code, "info", request, request, null, null, 1, "minimal", "{}");

    private static async Task EventuallyAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await predicate()) await Task.Delay(25, timeout.Token);
    }

    private sealed class ExportSender : ITelegramMessageSender
    {
        public int Calls { get; private set; }
        public long ChatId { get; private set; }

        public Task SendMessageAsync(long chatId, string text, string? replyMarkupJson, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task SendDocumentAsync(long chatId, Stream content, string name, CancellationToken cancellationToken)
        {
            Calls++;
            ChatId = chatId;
            Assert.Equal("ownday-diagnostics.zip", name);
            var header = new byte[2];
            await content.ReadExactlyAsync(header, cancellationToken);
            Assert.Equal(new byte[] { 0x50, 0x4b }, header);
            if (Calls == 1) throw new HttpRequestException();
        }
    }

    private sealed class FailedCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Synthetic commit failure.");
    }

    private sealed class ReadInterleaving(Func<CancellationToken, Task> write) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Ran && command.CommandText.Contains("learning_contexts", StringComparison.Ordinal))
            {
                Ran = true;
                await write(cancellationToken);
            }
            return result;
        }
    }

    private sealed class RejectFirstJournalBatch : DbCommandInterceptor
    {
        private int _attempts;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO diagnostic_events", StringComparison.Ordinal) &&
                Interlocked.Increment(ref _attempts) == 1)
                throw new NpgsqlException("Synthetic transient connection loss.", new IOException());
            return ValueTask.FromResult(result);
        }
    }
}
