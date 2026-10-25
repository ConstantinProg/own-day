using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfInboxStatisticsStore(OwnDayDbContext db) : IInboxStatisticsStore
{
    public async Task<InboxStatistics> ReadAsync(UserId userId, StatisticsPeriod period, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
        {
            if (db.Database.CurrentTransaction is null)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
                var result = await ReadCoreAsync(userId, period, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            if (db.Database.CurrentTransaction.GetDbTransaction().IsolationLevel is not
                (IsolationLevel.RepeatableRead or IsolationLevel.Serializable))
            {
                throw new InvalidOperationException("Statistics requires a Repeatable Read or Serializable transaction.");
            }
        }
        return await ReadCoreAsync(userId, period, cancellationToken);
    }

    private async Task<InboxStatistics> ReadCoreAsync(UserId userId, StatisticsPeriod period, CancellationToken cancellationToken)
    {
        var start = period.Start;
        var end = period.End;
        var items = db.InboxItems.AsNoTracking()
            .Where(i => i.UserId == userId && i.CapturedAt >= start && i.CapturedAt < end);
        var itemCounts = await items.GroupBy(i => i.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Status, g => g.Count, cancellationToken);
        var applications = db.InboxAutomaticApplications.AsNoTracking().Where(a => a.UserId == userId);
        var evaluated = applications.Where(a => a.EvaluatedAt >= start && a.EvaluatedAt < end);
        var autoApplied = await evaluated.Where(a => a.Route == InboxPolicyRoute.AutoApply)
            .Select(a => a.InboxItemId).Distinct().CountAsync(cancellationToken);

        // Policy JSON stays in the application format. Stream it without retaining the history or text payloads.
        var manualRequired = 0;
        long? previousItem = null;
        var countedItem = false;
        await foreach (var application in evaluated.Where(a => a.Route != InboxPolicyRoute.AutoApply)
            .OrderBy(a => a.InboxItemId).Select(a => new { a.InboxItemId, a.DecisionJson })
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            if (previousItem != application.InboxItemId)
            {
                previousItem = application.InboxItemId;
                countedItem = false;
            }
            if (!countedItem && JsonSerializer.Deserialize<InboxPolicyDecision>(application.DecisionJson, InboxAnalysisJson.Options)
                    is { } decision && !decision.Reasons.Contains(InboxPolicyReason.InboxInactive) &&
                !decision.Reasons.Contains(InboxPolicyReason.AnalysisSuperseded))
            {
                manualRequired++;
                countedItem = true;
            }
        }

        var events = db.InboxOperationEvents.AsNoTracking()
            .Where(e => e.UserId == userId.Value && e.At >= start && e.At < end);
        var eventCounts = await events.GroupBy(e => new { e.Code, e.Kind })
            .Select(g => new { g.Key.Code, g.Key.Kind, Count = g.Count() }).ToListAsync(cancellationToken);
        var analyses = db.InboxAnalyses.AsNoTracking().Where(a => a.UserId == userId &&
            a.CompletedAt >= start && a.CompletedAt < end);
        var analysisCounts = await analyses.GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Status, g => g.Count, cancellationToken);
        var attempts = db.OpenAIRequestAttempts.AsNoTracking()
            .Where(a => a.UserId == userId.Value && a.StartedAt >= start && a.StartedAt < end);
        var tokens = await attempts.GroupBy(a => 1).Select(g => new TokenStatistics(
            g.Sum(a => (long?)a.InputTokens) ?? 0,
            g.Sum(a => (long?)a.CachedInputTokens) ?? 0,
            g.Sum(a => (long?)a.OutputTokens) ?? 0,
            g.Sum(a => a.EstimatedUsd) ?? 0,
            g.Count(a => a.InputTokens == null), g.Count(a => a.CachedInputTokens == null),
            g.Count(a => a.OutputTokens == null), g.Count(a => a.EstimatedUsd == null)))
            .SingleOrDefaultAsync(cancellationToken) ?? TokenStatistics.Empty;
        var providerAttempts = await attempts.CountAsync(cancellationToken);
        var retryCount = await attempts.CountAsync(a => a.Sequence > 1, cancellationToken);
        var providerErrors = await attempts.CountAsync(a => a.Code != "response" && a.Code != "pending", cancellationToken);
        var staleBefore = end.AddMinutes(-10);
        var pending = await db.InboxAnalyses.CountAsync(a => a.UserId == userId && a.Status == InboxAnalysisStatus.Pending,
            cancellationToken);
        var stalled = await db.InboxAnalysisJobs.CountAsync(j => j.UserId == userId &&
            ((j.Status == InboxAnalysisJobStatus.Running && j.LeaseUntil <= end) ||
             (j.Status == InboxAnalysisJobStatus.Ready && j.AvailableAt < staleBefore)), cancellationToken);
        var unmetered = db.InboxAnalyses.AsNoTracking().Where(a => a.UserId == userId && a.CreatedAt >= start && a.CreatedAt < end &&
            !db.OpenAIRequestAttempts.Any(r => r.AnalysisId == a.Id));
        var legacyCount = 0;
        var legacyTokens = TokenStatistics.Empty;
        await foreach (var diagnostics in unmetered.Select(a => a.DiagnosticsJson)
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            legacyCount++;
            legacyTokens = legacyTokens.Add(ReadLegacyUsage(diagnostics));
        }
        var accountingStarted = await db.InboxOperationEvents.Where(e => e.Code == InboxEventCodes.ObservabilityStarted)
            .Select(e => (DateTime?)e.At).MinAsync(cancellationToken);
        var globalEvents = db.InboxOperationEvents.Where(e => e.UserId == 0 && e.At >= start && e.At < end);
        var backgroundErrorCodes = InboxEventCodes.BackgroundErrors.ToArray();
        var globalBackgroundErrors = await globalEvents.CountAsync(e => backgroundErrorCodes.Contains(e.Code), cancellationToken);
        var globalBackgroundRetries = await globalEvents.CountAsync(e => e.Code == InboxEventCodes.BackgroundDeliveryRetry, cancellationToken);
        var manualResolutions = await applications.CountAsync(a => a.ResolvedAt >= start && a.ResolvedAt < end, cancellationToken);
        var undone = await applications.CountAsync(a => a.UndoneAt >= start && a.UndoneAt < end, cancellationToken);
        var means = await ReadMeanDurationsAsync(userId, period, cancellationToken);

        return new InboxStatistics(
            Received: itemCounts.Values.Sum(),
            Processed: itemCounts.GetValueOrDefault(InboxItemStatus.Processed),
            Active: itemCounts.GetValueOrDefault(InboxItemStatus.Active),
            Discarded: itemCounts.GetValueOrDefault(InboxItemStatus.Discarded),
            AutoApplied: autoApplied,
            ManualRequired: manualRequired,
            ManualInterventions: eventCounts.Where(e => e.Code == InboxEventCodes.Manual).Sum(e => e.Count) + manualResolutions,
            Undone: undone,
            UndoRefused: eventCounts.Where(e => InboxEventCodes.UndoRefusals.Contains(e.Code)).Sum(e => e.Count),
            CreatedByKind: Enum.GetValues<InboxTargetKind>().Select(kind =>
                eventCounts.Where(e => e.Code == InboxEventCodes.Created && e.Kind == kind).Sum(e => e.Count)).ToArray(),
            AnalysisSucceeded: analysisCounts.GetValueOrDefault(InboxAnalysisStatus.Completed),
            AnalysisFailed: analysisCounts.Where(g => g.Key != InboxAnalysisStatus.Completed).Sum(g => g.Value),
            MeanAnalysisSeconds: means.Analysis,
            AnalysisPending: pending,
            ProviderAttempts: providerAttempts,
            Retries: retryCount,
            ProviderErrors: providerErrors,
            Stalled: stalled,
            LegacyUnmeteredAnalyses: legacyCount,
            Tokens: tokens,
            BackgroundRetries: eventCounts.Where(e => e.Code == InboxEventCodes.BackgroundRetry).Sum(e => e.Count),
            BackgroundErrors: eventCounts.Where(e => InboxEventCodes.BackgroundErrors.Contains(e.Code)).Sum(e => e.Count),
            GlobalBackgroundErrors: globalBackgroundErrors,
            GlobalBackgroundRetries: globalBackgroundRetries,
            LegacyTokens: legacyTokens,
            DetailedAccountingStartedAt: accountingStarted,
            MeanProcessingSeconds: means.Processing);
    }

    private async Task<(double? Analysis, double? Processing)> ReadMeanDurationsAsync(
        UserId userId, StatisticsPeriod period, CancellationToken cancellationToken)
    {
        var start = period.Start;
        var end = period.End;
        if (db.Database.IsNpgsql())
        {
            var analysis = await db.InboxAnalyses.Where(a => a.UserId == userId && a.Status == InboxAnalysisStatus.Completed &&
                    a.CompletedAt >= start && a.CompletedAt < end)
                .AverageAsync(a => (double?)(a.CompletedAt!.Value - a.CreatedAt).TotalSeconds, cancellationToken);
            var processing = await db.InboxItems.Where(i => i.UserId == userId && i.CapturedAt >= start && i.CapturedAt < end &&
                    i.ProcessedAt != null)
                .AverageAsync(i => (double?)(i.ProcessedAt!.Value - i.CapturedAt).TotalSeconds, cancellationToken);
            return (analysis, processing);
        }
        // SQLite fixtures lack interval arithmetic; both averages still execute in the database.
        var sqliteAnalysis = await db.Database.SqlQuery<double?>($"""
            SELECT AVG((julianday(completed_at) - julianday(created_at)) * 86400.0) AS "Value"
            FROM inbox_analyses WHERE user_id = {userId.Value} AND status = {(int)InboxAnalysisStatus.Completed}
                AND completed_at >= {start} AND completed_at < {end}
            """).SingleAsync(cancellationToken);
        var sqliteProcessing = await db.Database.SqlQuery<double?>($"""
            SELECT AVG((julianday(processed_at) - julianday(captured_at)) * 86400.0) AS "Value"
            FROM inbox_items WHERE user_id = {userId.Value} AND captured_at >= {start} AND captured_at < {end}
                AND processed_at IS NOT NULL
            """).SingleAsync(cancellationToken);
        return (sqliteAnalysis, sqliteProcessing);
    }

    private static OpenAIRequestAttempt ReadLegacyUsage(string diagnostics)
    {
        try
        {
            using var document = JsonDocument.Parse(diagnostics);
            var root = document.RootElement;
            return new()
            {
                InputTokens = Read("inputTokens"), CachedInputTokens = Read("cachedInputTokens"), OutputTokens = Read("outputTokens")
            };

            int? Read(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value) && value >= 0 ? value : null;
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
