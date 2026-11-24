using System.Data;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Application.TesterMode;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Infrastructure.Learning;
using OwnDay.Infrastructure.OpenAIAnalysis;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.Diagnostics;

internal sealed record DiagnosticExportResult(string? Name, byte[]? Bytes, string? Error = null);

internal sealed partial class DiagnosticExportReader(OwnDayDbContext db, TesterModeService tester, DiagnosticText text,
    DiagnosticJournal journal, IOptions<DiagnosticOptions> options, IOptions<LearningRequestProcessingOptions> learning,
    IOptions<OpenAIInboxAnalysisOptions> provider, IOptions<OpenAICostOptions> budget, IHostEnvironment environment,
    ApplicationVersion version, ICurriculumStore curriculum)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal async Task<DiagnosticExportResult> BuildAsync(DiagnosticExportJob job, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
        var owner = new UserId(job.OwnerId);
        if (!await tester.CanExportAsync(owner, token)) return new(null, null, "access_revoked");
        var now = await db.Database.SqlQuery<DateTime>($"SELECT transaction_timestamp() AS \"Value\"").SingleAsync(token);
        var cutoff = job.Mode == DiagnosticExportMode.Recent ? now.AddHours(-24) : now.AddDays(-DiagnosticOptions.RetentionDays);
        var requests = db.Set<LearningRequest>().AsNoTracking().Where(row => row.UserId == owner);
        var events = db.Set<DiagnosticEvent>().AsNoTracking().Where(row => row.OwnerId == owner.Value &&
            row.OccurredAt >= cutoff && row.OccurredAt <= now);
        var receipt = job.Mode == DiagnosticExportMode.Last
            ? await FindLastReceiptAsync(owner, requests, token)
            : null;
        if (job.Mode == DiagnosticExportMode.Last && receipt is null)
            return new(null, null, "no_requests");
        if (receipt is not null)
            events = events.Where(row => row.CorrelationId == receipt.CorrelationId ||
                receipt.RequestId != null && row.RequestId == receipt.RequestId);
        if (job.Mode == DiagnosticExportMode.Recent)
            events = events.OrderByDescending(row => row.OccurredAt).ThenByDescending(row => row.Id).Take(100);

        using var output = new MemoryStream();
        using var cappedOutput = new DiagnosticLimitedStream(output, options.Value.MaxExportBytes);
        if (job.Mode == DiagnosticExportMode.Full)
        {
            using var zip = new ZipArchive(cappedOutput, ZipArchiveMode.Create, leaveOpen: true);
            await using var entry = zip.CreateEntry("diagnostics.json", CompressionLevel.Fastest).Open();
            using var cappedJson = new DiagnosticLimitedStream(entry, options.Value.MaxExportBytes);
            await WriteDocumentAsync(cappedJson, transaction, job, owner, now, cutoff, receipt, requests, events, token);
        }
        else
        {
            await WriteDocumentAsync(cappedOutput, transaction, job, owner, now, cutoff, receipt, requests, events, token);
        }
        await transaction.CommitAsync(token);
        return new(job.Mode == DiagnosticExportMode.Full ? "ownday-diagnostics.zip" : "ownday-diagnostics.json", output.ToArray());
    }

    private async Task<ExportReceipt?> FindLastReceiptAsync(UserId owner, IQueryable<LearningRequest> requests,
        CancellationToken token)
    {
        var acceptedUpdates = db.ProcessedTelegramUpdates.AsNoTracking()
            .Where(row => row.DiagnosticOwnerId == owner.Value && row.DiagnosticCorrelationId != null && row.ProcessedAt != null);
        var acceptedUpdate = await acceptedUpdates.OrderByDescending(row => row.ReceivedAt).ThenByDescending(row => row.UpdateId)
            .Select(row => new { Correlation = row.DiagnosticCorrelationId!.Value, row.ReceivedAt }).FirstOrDefaultAsync(token);
        var legacyReceipt = await requests.Where(row => !acceptedUpdates.Any(update => update.DiagnosticCorrelationId == row.Id))
            .OrderByDescending(row => row.ReceivedAt).ThenByDescending(row => row.Sequence)
            .Select(row => new { row.Id, row.ReceivedAt }).FirstOrDefaultAsync(token);
        if (acceptedUpdate is null || legacyReceipt is not null && legacyReceipt.ReceivedAt > acceptedUpdate.ReceivedAt)
            return legacyReceipt is null ? null : new(legacyReceipt.Id, legacyReceipt.Id);

        var requestId = await requests.Where(row => row.Id == acceptedUpdate.Correlation)
            .Select(row => (Guid?)row.Id).SingleOrDefaultAsync(token);
        return new(acceptedUpdate.Correlation, requestId);
    }

    private async Task WriteDocumentAsync(Stream stream, IDbContextTransaction transaction, DiagnosticExportJob job,
        UserId owner, DateTime now, DateTime cutoff, ExportReceipt? receipt, IQueryable<LearningRequest> requests,
        IQueryable<DiagnosticEvent> events, CancellationToken token)
    {
        var completeness = new SortedDictionary<string, Section>(StringComparer.Ordinal);
        var summary = new TimelineSummary();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        Property(writer, "metadata", new
        {
            schemaVersion = 1, mode = job.Mode, snapshotAtUtc = now, historyFromUtc = cutoff,
            historyToUtc = now, ownerId = owner.Value, applicationVersion = version.Version, version.CommitSha,
            requestId = receipt?.RequestId, correlationId = receipt?.CorrelationId
        });

        writer.WriteStartObject("snapshot");
        var requestSlice = receipt is null ? requests : requests.Where(row => row.Id == receipt.RequestId);
        await ValueAsync(writer, transaction, completeness, "userSettings", async () =>
        {
            var locale = await db.TelegramUserLanguages.Where(row => row.UserId == owner.Value)
                .Select(row => row.Locale).SingleOrDefaultAsync(token);
            return new { persistedLanguage = locale, effectiveLanguage = locale ?? (job.InRussian ? "ru" : "en"),
                source = locale is null ? "export_receipt" : "persisted" };
        }, token);
        await ValueAsync(writer, transaction, completeness, "llmBudget", () => OwnerBudgetAsync(owner, now, token), token);
        await WriteSnapshotAsync(writer, transaction, completeness, owner, requestSlice, receipt?.CorrelationId, token);
        writer.WriteEndObject();

        await RowsAsync(writer, transaction, completeness, "timeline", events.OrderBy(row => row.OccurredAt).ThenBy(row => row.Id),
            row =>
            {
                summary.Count(row);
                return new
                {
                    row.Id, row.OwnerId, row.OccurredAt, row.Category, row.Code, row.Severity, row.CorrelationId,
                    operationId = row.Category == "llm" ? row.EntityId : row.RequestId ?? row.CorrelationId,
                    row.RequestId, row.AttemptNumber, row.EntityId, row.SchemaVersion, row.Detail,
                    data = JsonSerializer.Deserialize<JsonElement>(row.Data)
                };
            }, token);

        await ValueAsync(writer, transaction, completeness, "configuration", async () =>
        {
            var schema = (await db.Database.GetAppliedMigrationsAsync(token)).LastOrDefault();
            return new
            {
                environment = text.Filter(environment.EnvironmentName), databaseSchema = schema, testerMode = tester.Enabled,
                llm = new { provider = "openai", provider.Value.Enabled, model = text.Filter(provider.Value.Model),
                    provider.Value.MaxOutputTokens, provider.Value.MaxInputUtf8Bytes, provider.Value.TimeoutSeconds,
                    provider.Value.EffectiveReasoningEffort,
                    budget = new { budget.Value.MonthlyUsdLimit, budget.Value.TokensPerRequest, budget.Value.MonthlyTokenLimit,
                        budget.Value.MonthlyUserTokenLimit, budget.Value.MonthlyCallLimit, budget.Value.MonthlyUserCallLimit,
                        budget.Value.TokenLimit } },
                learning = new
                {
                    learning.Value.Enabled, learning.Value.DurableIngressEnabled, learning.Value.DialogueEnabled,
                    learning.Value.ConsultationEnabled, learning.Value.PracticeEnabled, learning.Value.EnglishDiagnosticEnabled,
                    learning.Value.ProgramSwitchEnabled, learning.Value.AcceptancePaused, learning.Value.MaxQueueAgeSeconds,
                    learning.Value.Parallelism, learning.Value.PollMilliseconds, learning.Value.LeaseSeconds,
                    learning.Value.MaxAttempts, learning.Value.ProviderTimeoutSeconds, learning.Value.MaxProviderAttempts
                },
                telegram = new { transport = "webhook", exportChat = "private", documentTimeoutSeconds = 30, documentAttempts = 3 },
                diagnostics = new { options.Value.QueueCapacity, options.Value.BatchSize, options.Value.MaxExportBytes,
                    options.Value.ExportTimeoutSeconds, options.Value.DocumentLifetimeMinutes, retentionDays = DiagnosticOptions.RetentionDays }
            };
        }, token);

        writer.WriteStartObject("summary");
        await ValueAsync(writer, transaction, completeness, "currentState", async () =>
        {
            var dialogue = await db.Set<DialogueContext>().AsNoTracking().SingleOrDefaultAsync(row => row.UserId == owner, token);
            return new
            {
                requests = await RequestCountsAsync(requests, token),
                incompleteDialogues = dialogue is { ExpectedResponse: not DialogueExpectedResponse.None } ? 1 : 0,
                pendingConfirmations = dialogue?.PendingConfirmation is null ? 0 : 1
            };
        }, token);
        await ValueAsync(writer, transaction, completeness, "requestCohort", async () =>
            await RequestCountsAsync(requestSlice.Where(row => row.ReceivedAt >= cutoff && row.ReceivedAt <= now), token), token);
        Property(writer, "selectedTimeline", summary);
        await ValueAsync(writer, transaction, completeness, "loss", async () =>
        {
            var generation = await db.Set<DiagnosticFence>().AsNoTracking().Where(row => row.OwnerId == owner.Value)
                .Select(row => (Guid?)row.Generation).SingleOrDefaultAsync(token) ?? Guid.Empty;
            return new { observed = journal.ObservedLosses(owner.Value, generation), scope = "owner_on_this_process_at_serialization" };
        }, token);
        Property(writer, "incompleteSections", completeness.Where(pair => pair.Value.Status != "complete").Select(pair => pair.Key).ToArray());
        writer.WriteEndObject();
        Property(writer, "completeness", new
        {
            sections = completeness,
            journal = new { status = "partial", code = "asynchronous_best_effort",
                reason = "Only events committed at snapshot time are included. Buffering, restart, or overload can leave gaps." }
        });
        writer.WriteEndObject();
        await writer.FlushAsync(token);
    }

    private async Task<object> OwnerBudgetAsync(UserId owner, DateTime now, CancellationToken token)
    {
        var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);
        var carried = await db.Set<OpenAIBudgetCarryover>().AsNoTracking().SingleOrDefaultAsync(row =>
            row.UserId == owner.Value && row.Month == DateOnly.FromDateTime(start), token);
        var usage = await db.OpenAIRequestAttempts.Where(row => row.UserId == owner.Value &&
            row.StartedAt >= start && row.StartedAt < end).GroupBy(row => row.UserId).Select(rows => new
            {
                Calls = rows.LongCount(), Charged = rows.Sum(row => row.ChargedTokens),
                RecordedTokens = rows.Sum(row => (long)(row.InputTokens ?? 0) + (row.OutputTokens ?? 0)),
                PendingKnownUsd = rows.Sum(row => row.FinishedAt == null ? row.ReservedUsd ?? 0 : 0),
                PendingUnpriced = rows.LongCount(row => row.FinishedAt == null && row.ReservedUsd == null),
                PendingTokens = rows.Sum(row => row.FinishedAt == null ? row.ReservedTokens : 0),
                MissingEstimate = rows.LongCount(row => row.EstimatedUsd == null),
                KnownEstimateUsd = rows.Sum(row => row.EstimatedUsd ?? 0)
            }).SingleOrDefaultAsync(token);
        var charged = (usage?.Charged ?? 0) + (carried?.ChargedTokens ?? 0);
        var calls = (usage?.Calls ?? 0) + (carried?.CallCount ?? 0);
        return new { periodStartUtc = start, periodEndExclusiveUtc = end, calls, chargedTokens = charged,
            retainedRecordedTokens = usage?.RecordedTokens ?? 0, pendingReservedTokens = usage?.PendingTokens ?? 0,
            knownPendingReservedUsd = usage?.PendingKnownUsd ?? 0, pendingUnpricedCalls = usage?.PendingUnpriced ?? 0,
            retainedCallsWithoutCostEstimate = usage?.MissingEstimate ?? 0, knownRetainedEstimateUsd = usage?.KnownEstimateUsd ?? 0,
            carriedCalls = carried?.CallCount ?? 0, carryoverHasUnpricedCharges = carried?.HasUnknownCost ?? false,
            remainingOwnerTokens = Math.Max(0, budget.Value.MonthlyUserTokenLimit - charged),
            remainingOwnerCalls = Math.Max(0, budget.Value.MonthlyUserCallLimit - calls) };
    }

    private sealed record ExportReceipt(Guid CorrelationId, Guid? RequestId);

    private static async Task<RequestCounts> RequestCountsAsync(IQueryable<LearningRequest> requests, CancellationToken token)
    {
        var counts = await requests.GroupBy(row => new { row.Status, row.Outcome })
            .Select(group => new { group.Key.Status, group.Key.Outcome, Count = group.Count() }).ToListAsync(token);
        return new(
            counts.Where(row => row.Status == LearningRequestStatus.Completed && IsSuccessful(row.Outcome)).Sum(row => row.Count),
            counts.Where(row => row.Status == LearningRequestStatus.Completed && !IsSuccessful(row.Outcome)).Sum(row => row.Count),
            counts.Where(row => row.Status != LearningRequestStatus.Completed).Sum(row => row.Count));
    }

    private static bool IsSuccessful(string? outcome) => outcome is not (null or "attempts_exhausted" or
        "provider_outcome_unknown" or "storage_unavailable" or "unsupported_payload" or "stale_target") &&
        !outcome.Contains("failed", StringComparison.Ordinal) && !outcome.Contains("unavailable", StringComparison.Ordinal) &&
        !outcome.Contains("rejected", StringComparison.Ordinal);

    private static void Property<T>(Utf8JsonWriter writer, string key, T value)
    {
        writer.WritePropertyName(key);
        JsonSerializer.Serialize(writer, value, Json);
    }

    private static async Task RowsAsync<T>(Utf8JsonWriter writer, IDbContextTransaction transaction,
        IDictionary<string, Section> completeness, string name, IQueryable<T> query, Func<T, object> project, CancellationToken token)
    {
        await transaction.CreateSavepointAsync("section", token);
        var opened = false;
        try
        {
            await foreach (var row in query.AsAsyncEnumerable().WithCancellation(token))
            {
                var value = JsonSerializer.SerializeToElement(project(row), Json);
                if (!opened)
                {
                    writer.WriteStartArray(name);
                    opened = true;
                }
                value.WriteTo(writer);
                await writer.FlushAsync(token);
            }
            if (!opened) writer.WriteStartArray(name);
            writer.WriteEndArray();
            completeness[name] = new("complete");
        }
        catch (Exception error) when (IsSectionFailure(error) && !token.IsCancellationRequested)
        {
            await transaction.RollbackToSavepointAsync("section", token);
            if (opened) writer.WriteEndArray();
            else writer.WriteNull(name);
            completeness[name] = new(opened ? "partial" : "unavailable", "section_read_failed", "A snapshot read failed.");
        }
        await transaction.ReleaseSavepointAsync("section", token);
    }

    private static async Task ValueAsync(Utf8JsonWriter writer, IDbContextTransaction transaction,
        IDictionary<string, Section> completeness, string name, Func<Task<object>> read, CancellationToken token)
    {
        await transaction.CreateSavepointAsync("section", token);
        try
        {
            var value = JsonSerializer.SerializeToElement(await read(), Json);
            writer.WritePropertyName(name);
            value.WriteTo(writer);
            completeness[name] = new("complete");
        }
        catch (Exception error) when (IsSectionFailure(error) && !token.IsCancellationRequested)
        {
            await transaction.RollbackToSavepointAsync("section", token);
            writer.WriteNull(name);
            completeness[name] = new("unavailable", "section_read_failed", "A snapshot read failed.");
        }
        await transaction.ReleaseSavepointAsync("section", token);
    }

    private static bool IsSectionFailure(Exception error) =>
        error is Npgsql.NpgsqlException or JsonException or InvalidOperationException;

    private sealed record Section(string Status, string? Code = null, string? Reason = null);
    private sealed record RequestCounts(int SuccessfulRequests, int FailedRequests, int PendingRequests);

    private sealed class TimelineSummary
    {
        private readonly HashSet<Guid> _timeouts = [];
        private readonly HashSet<Guid> _cancellations = [];
        private readonly HashSet<Guid> _unknown = [];
        public int TimedOutAttempts => _timeouts.Count;
        public int CancelledAttempts => _cancellations.Count;
        public int UnknownOutcomeAttempts => _unknown.Count;
        public int SeverityErrorEvents => Errors;
        public int Events { get; private set; }
        public int Errors { get; private set; }
        public int Warnings { get; private set; }
        public int Retries { get; private set; }
        public int LlmCalls { get; private set; }
        public int ValidationErrors { get; private set; }
        public int RejectedDecisions { get; private set; }
        public int TelegramErrors { get; private set; }

        internal void Count(DiagnosticEvent row)
        {
            Events++;
            if (row.Category == "llm" && row.EntityId is { } attempt &&
                row.Code is "provider_outcome_unknown" or "provider_attempt_finished" or "llm_call_failed")
            {
                using var data = JsonDocument.Parse(row.Data);
                if (data.RootElement.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
                {
                    string? Field(string name) => fields.TryGetProperty(name, out var value) &&
                        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    if (Field("cancellationSource") is "adapter_deadline" or "worker_deadline" or
                        "caller_and_worker_deadline" or "worker_shutdown_and_worker_deadline")
                        _timeouts.Add(attempt);
                    if (Field("status") == "cancelled" || Field("cancellationSource") is not null)
                        _cancellations.Add(attempt);
                    if (row.Code == "provider_outcome_unknown" || Field("status") is "cancelled" or "outcome_unknown")
                        _unknown.Add(attempt);
                }
            }
            if (row.Severity == "error") Errors++;
            if (row.Severity == "warning") Warnings++;
            if (row.Code is "request_reclaimed" or "provider_retry_scheduled" or "telegram_retry_scheduled") Retries++;
            if (row.Code == "llm_call_started") LlmCalls++;
            if (row.Code == "llm_validation_failed") ValidationErrors++;
            if (row.Code == "decision_rejected") RejectedDecisions++;
            if (row.Code == "telegram_failed") TelegramErrors++;
        }
    }
}
