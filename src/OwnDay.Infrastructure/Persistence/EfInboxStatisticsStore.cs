using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfInboxStatisticsStore(OwnDayDbContext db, IServiceScopeFactory? scopes = null) : IInboxStatisticsStore
{
    public async Task<InboxStatistics> ReadAsync(UserId userId, StatisticsPeriod period, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql() && scopes is not null)
        {
            // The webhook's write transaction uses Read Committed. Statistics gets a separate coherent snapshot.
            await using var scope = scopes.CreateAsyncScope();
            var snapshot = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            await using var transaction = await snapshot.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            var result = await ReadCoreAsync(snapshot, userId, period, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is null)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            var result = await ReadCoreAsync(db, userId, period, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        return await ReadCoreAsync(db, userId, period, cancellationToken);
    }

    private static async Task<InboxStatistics> ReadCoreAsync(OwnDayDbContext db, UserId userId, StatisticsPeriod period, CancellationToken cancellationToken)
    {
        var start = period.Start;
        var end = period.End;
        // Projection deliberately excludes original text, context, proposal payloads and provider bodies.
        var items = await db.InboxItems.AsNoTracking()
            .Where(i => i.UserId == userId && i.CapturedAt >= start && i.CapturedAt < end)
            .Select(i => new { i.Status, i.CapturedAt, i.ProcessedAt }).ToListAsync(cancellationToken);
        var applications = db.InboxAutomaticApplications.AsNoTracking().Where(a => a.UserId == userId);
        var evaluated = await applications.Where(a => a.EvaluatedAt >= start && a.EvaluatedAt < end)
            .Select(a => new { a.InboxItemId, a.Route, a.DecisionJson }).ToListAsync(cancellationToken);
        var events = await db.InboxOperationEvents.AsNoTracking()
            .Where(e => e.UserId == userId.Value && e.At >= start && e.At < end)
            .Select(e => new { e.Code, e.Kind }).ToListAsync(cancellationToken);
        var analyses = await db.InboxAnalyses.AsNoTracking().Where(a => a.UserId == userId &&
                a.CompletedAt >= start && a.CompletedAt < end)
            .Select(a => new { a.Status, a.CreatedAt, a.CompletedAt }).ToListAsync(cancellationToken);
        var attempts = await db.OpenAIRequestAttempts.AsNoTracking()
            .Where(a => a.UserId == userId.Value && a.StartedAt >= start && a.StartedAt < end)
            .ToListAsync(cancellationToken);
        var retryCount = await db.OpenAIRequestAttempts.CountAsync(a => a.UserId == userId.Value &&
            a.StartedAt >= start && a.StartedAt < end && a.Sequence > 1, cancellationToken);
        var durations = analyses.Where(a => a.Status == InboxAnalysisStatus.Completed)
            .Select(a => (a.CompletedAt!.Value - a.CreatedAt).TotalSeconds).ToArray();
        var staleBefore = end.AddMinutes(-10);
        var pending = await db.InboxAnalyses.CountAsync(a => a.UserId == userId && a.Status == InboxAnalysisStatus.Pending,
            cancellationToken);
        var stalled = await db.InboxAnalysisJobs.CountAsync(j => j.UserId == userId &&
            ((j.Status == InboxAnalysisJobStatus.Running && j.LeaseUntil <= end) ||
             (j.Status == InboxAnalysisJobStatus.Ready && j.AvailableAt < staleBefore)), cancellationToken);
        var unmeteredDiagnostics = await db.InboxAnalyses.Where(a => a.UserId == userId && a.CreatedAt >= start && a.CreatedAt < end &&
            !db.OpenAIRequestAttempts.Any(r => r.AnalysisId == a.Id)).Select(a => a.DiagnosticsJson).ToListAsync(cancellationToken);
        var accountingStarted = await db.InboxOperationEvents.Where(e => e.Code == "observability_started")
            .Select(e => (DateTime?)e.At).MinAsync(cancellationToken);
        var processingDurations = items.Where(i => i.ProcessedAt is not null)
            .Select(i => (i.ProcessedAt!.Value - i.CapturedAt).TotalSeconds).ToArray();
        return new(items.Count, items.Count(i => i.Status == InboxItemStatus.Processed), items.Count(i => i.Status == InboxItemStatus.Active),
            items.Count(i => i.Status == InboxItemStatus.Discarded),
            evaluated.Where(a => a.Route == InboxPolicyRoute.AutoApply).Select(a => a.InboxItemId).Distinct().Count(),
            evaluated.Where(a => a.Route != InboxPolicyRoute.AutoApply &&
                JsonSerializer.Deserialize<InboxPolicyDecision>(a.DecisionJson, InboxAnalysisJson.Options) is { } decision &&
                !decision.Reasons.Contains(InboxPolicyReason.InboxInactive) &&
                !decision.Reasons.Contains(InboxPolicyReason.AnalysisSuperseded)).Select(a => a.InboxItemId).Distinct().Count(),
            events.Count(e => e.Code == "manual") + await applications.CountAsync(a => a.ResolvedAt >= start && a.ResolvedAt < end, cancellationToken),
            await applications.CountAsync(a => a.UndoneAt >= start && a.UndoneAt < end, cancellationToken),
            events.Count(e => e.Code.StartsWith("undo_", StringComparison.Ordinal)),
            Enumerable.Range(0, 5).Select(kind => events.Count(e => e.Code == "created" && (int?)e.Kind == kind)).ToArray(),
            analyses.Count(a => a.Status == InboxAnalysisStatus.Completed), analyses.Count(a => a.Status != InboxAnalysisStatus.Completed),
            durations.Length == 0 ? null : durations.Average(), pending,
            attempts.Count, retryCount, attempts.Count(a => a.Code is not "response" and not "pending"), stalled, unmeteredDiagnostics.Count,
            TokenStatistics.Aggregate(attempts), events.Count(e => e.Code == "background_retry"),
            events.Count(e => e.Code.StartsWith("background_", StringComparison.Ordinal) && e.Code.EndsWith("_error", StringComparison.Ordinal)),
            await db.InboxOperationEvents.CountAsync(e => e.UserId == 0 && e.At >= start && e.At < end &&
                e.Code.StartsWith("background_") && e.Code.EndsWith("_error"), cancellationToken),
            await db.InboxOperationEvents.CountAsync(e => e.UserId == 0 && e.At >= start && e.At < end &&
                e.Code == "background_delivery_retry", cancellationToken),
            TokenStatistics.Aggregate(unmeteredDiagnostics.Select(ReadLegacyUsage)), accountingStarted,
            processingDurations.Length == 0 ? null : processingDurations.Average());
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
