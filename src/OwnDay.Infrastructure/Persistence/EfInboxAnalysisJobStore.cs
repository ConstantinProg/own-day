using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Domain.Inbox;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfInboxAnalysisJobStore(OwnDayDbContext db, IInboxProcessingStore inboxLocks) : IInboxAnalysisJobStore
{
    public async Task<InboxAnalysisLease?> ClaimAsync(TimeSpan lease, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid();
        // One autocommit statement. Database time avoids clock skew between hosts.
        var rows = await db.InboxAnalysisJobs.FromSqlInterpolated($"""
            UPDATE inbox_analysis_jobs SET status = {(int)InboxAnalysisJobStatus.Running}, lease_token = {token},
                lease_until = clock_timestamp() + {lease}, available_at = clock_timestamp() + {lease},
                attempt_count = attempt_count + 1
            WHERE id = (SELECT id FROM inbox_analysis_jobs
                WHERE status IN ({(int)InboxAnalysisJobStatus.Ready}, {(int)InboxAnalysisJobStatus.Running}) AND available_at <= clock_timestamp()
                ORDER BY available_at, id FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING *
            """).AsNoTracking().ToListAsync(cancellationToken);
        var job = rows.SingleOrDefault();
        return job is null ? null : new(job.Id, token, job.UserId, job.InboxItemId, job.AttemptCount);
    }

    public async Task<InboxAnalysis?> StartAsync(InboxAnalysisLease lease, InboxAnalyzerDescriptor descriptor,
        string contextJson, string contextFingerprint, string inputFingerprint, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await LockAsync(lease, cancellationToken);
        if (job is null)
        {
            return null;
        }

        var result = await inboxLocks.ExecuteLockedAsync<InboxAnalysis?>(job.UserId, job.InboxItemId, async (item, token) =>
        {
            if (item?.Status != InboxItemStatus.Active)
            {
                return null;
            }

            var now = await NowAsync(token);
            if (job.LeaseUntil <= now)
            {
                return null;
            }

            if (job.AnalysisId is { } id)
            {
                return await db.InboxAnalyses.Include(row => row.Proposals).SingleAsync(row => row.Id == id, token);
            }

            var version = (await db.InboxAnalyses.Where(row => row.InboxItemId == job.InboxItemId)
                .MaxAsync(row => (int?)row.Version, token) ?? 0) + 1;
            var analysis = InboxAnalysis.Start(job.UserId, job.InboxItemId, version, descriptor, contextJson,
                contextFingerprint, inputFingerprint, now);
            db.InboxAnalyses.Add(analysis);
            job.AttachAnalysis(analysis);
            return analysis;
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<InboxAnalysisJobResult?> FinishAsync(InboxAnalysisLease lease, InboxAnalysisOutcome outcome, TimeSpan? retryAfter,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await LockAsync(lease, cancellationToken);
        if (job is null)
        {
            return null;
        }

        var result = await inboxLocks.ExecuteLockedAsync<InboxAnalysisJobResult?>(job.UserId, job.InboxItemId, async (item, token) =>
        {
            var now = await NowAsync(token);
            if (job.LeaseUntil <= now)
            {
                return null;
            }

            var analysis = job.AnalysisId is { } id
                ? await db.InboxAnalyses.Include(row => row.Proposals).SingleAsync(row => row.Id == id, token) : null;
            return job.Finish(item, analysis, outcome, retryAfter, now);
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    // Interrupted operator invocations from before/after this migration must also reach a terminal state.
    // Ten minutes exceeds the configured provider timeout; automatic analyses are owned by their durable job.
    public async Task RecoverAsync(bool processingEnabled, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE inbox_analyses a SET status = {(int)InboxAnalysisStatus.Failed}, completed_at = clock_timestamp(),
                diagnostics = {InboxAnalysisOutcome.Failure(InboxAnalysisCode.Interrupted).DiagnosticsJson}::jsonb
            WHERE status = {(int)InboxAnalysisStatus.Pending} AND created_at < clock_timestamp() - interval '10 minutes'
                AND NOT EXISTS (SELECT 1 FROM inbox_analysis_jobs j WHERE j.analysis_id = a.id)
            """, cancellationToken);
        if (!processingEnabled)
        {
            // Stop interrupted attempts when AI has been switched off. Untouched Ready jobs remain queued.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH stopped AS (
                    UPDATE inbox_analysis_jobs SET status = {(int)InboxAnalysisJobStatus.Failed}, lease_token = NULL, lease_until = NULL,
                        last_code = {InboxAnalysisCode.Disabled.ToStorageValue()} WHERE
                        (status = {(int)InboxAnalysisJobStatus.Running} AND lease_until <= clock_timestamp()) OR (status = {(int)InboxAnalysisJobStatus.Ready} AND analysis_id IS NOT NULL)
                    RETURNING analysis_id)
                UPDATE inbox_analyses SET status = {(int)InboxAnalysisStatus.Failed}, completed_at = clock_timestamp(),
                    diagnostics = {InboxAnalysisOutcome.Failure(InboxAnalysisCode.Disabled).DiagnosticsJson}::jsonb
                WHERE status = {(int)InboxAnalysisStatus.Pending} AND id IN (SELECT analysis_id FROM stopped)
                """, cancellationToken);
        }
    }

    private async Task<InboxAnalysisJob?> LockAsync(InboxAnalysisLease lease, CancellationToken token)
    {
        var rows = await db.InboxAnalysisJobs.FromSqlInterpolated($"""
            SELECT * FROM inbox_analysis_jobs WHERE id = {lease.JobId} AND lease_token = {lease.Token}
                AND status = {(int)InboxAnalysisJobStatus.Running} AND lease_until > clock_timestamp() FOR UPDATE
            """).ToListAsync(token);
        var job = rows.SingleOrDefault();
        if (job is not null)
        {
            await db.Entry(job).ReloadAsync(token);
        }

        return job?.UserId == lease.UserId && job.InboxItemId == lease.ItemId ? job : null;
    }

    private Task<DateTime> NowAsync(CancellationToken token) =>
        db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
}
