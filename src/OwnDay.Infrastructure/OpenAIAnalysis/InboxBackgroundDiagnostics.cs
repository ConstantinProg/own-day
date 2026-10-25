using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.OpenAIAnalysis;

public sealed class InboxBackgroundDiagnostics(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<InboxBackgroundDiagnostics> logger)
{
    public async Task RecordAsync(string code, InboxAnalysisLease? lease, CancellationToken cancellationToken,
        InboxApplicationCandidate? application = null)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            db.InboxOperationEvents.Add(new()
            {
                UserId = lease?.UserId.Value ?? application?.UserId.Value ?? 0,
                InboxItemId = lease?.ItemId ?? application?.InboxItemId ?? 0,
                Code = code, At = clock.GetUtcNow().UtcDateTime
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Database failures can only be logged; never claim complete durable diagnostics during an outage.
            logger.LogWarning("Could not persist Inbox diagnostic {Code}, job {JobId} ({ErrorType}).",
                code, lease?.JobId, exception.GetType().Name);
        }
    }

    public async Task LogOverdueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;
        var threshold = now.AddMinutes(-10);
        var jobs = await db.InboxAnalysisJobs.AsNoTracking().Where(j =>
                (j.Status == InboxAnalysisJobStatus.Running && j.LeaseUntil <= now) ||
                (j.Status == InboxAnalysisJobStatus.Ready && j.AvailableAt < threshold))
            .Select(j => new { j.Id, j.InboxItemId, j.AnalysisId, j.AttemptCount, j.LastCode, j.LeaseUntil })
            .Take(50).ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            logger.LogWarning("Overdue Inbox job {JobId}, item {InboxItemId}, analysis {AnalysisId}, attempts {Attempts}, last {Code}, lease {LeaseUntil}.",
                job.Id, job.InboxItemId, job.AnalysisId, job.AttemptCount, job.LastCode, job.LeaseUntil);
        }
        var requests = await db.OpenAIRequestAttempts.AsNoTracking()
            .Where(a => a.FinishedAt == null && a.StartedAt < threshold)
            .Select(a => new { a.Id, a.AnalysisId, a.StartedAt, a.ReservedTokens }).Take(50).ToListAsync(cancellationToken);
        foreach (var request in requests)
        {
            logger.LogWarning("Unresolved OpenAI request {AttemptId}, analysis {AnalysisId}, started {StartedAt}, retained reservation {Tokens}.",
                request.Id, request.AnalysisId, request.StartedAt, request.ReservedTokens);
        }
    }
}
