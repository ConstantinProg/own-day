using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning.Requests;
using OwnDay.Infrastructure.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfLearningRequestStore(OwnDayDbContext db) : ILearningRequestStore
{
    public sealed record AttemptAdmission(Guid? Id, bool Unknown, bool OwnershipLost);

    public async Task<AttemptAdmission> BeginAttemptAsync(LearningRequestLease lease, int ordinal, int maxAttempts,
        TimeSpan renewal, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lease.UserId.Value})", token);
        var fenced = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE learning_requests SET lease_until = clock_timestamp() + {renewal}
            WHERE id = {lease.RequestId} AND user_id = {lease.UserId.Value} AND status = 2
                AND lease_token = {lease.Token} AND attempt_count = {lease.Attempt} AND lease_until > clock_timestamp()
            """, token);
        if (fenced == 0) return new(null, false, true);
        var attempts = await db.Set<LearningProviderAttempt>().Where(a => a.RequestId == lease.RequestId && a.Ordinal == ordinal)
            .OrderBy(a => a.Number).ToListAsync(token);
        if (attempts.Any(a => a.FinishedAt is null || a.Outcome == "unexpected_failure")) return new(null, true, false);
        if (attempts.Count >= maxAttempts || attempts.LastOrDefault()?.Outcome == "permanent_failure") return new(null, false, false);
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
        var attempt = new LearningProviderAttempt
        {
            RequestId = lease.RequestId, Ordinal = ordinal, Number = attempts.Count + 1,
            LeaseToken = lease.Token, StartedAt = now
        };
        db.Add(attempt);
        await db.SaveChangesAsync(token);
        var stillOwned = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE learning_requests SET lease_token = lease_token WHERE id = {lease.RequestId}
                AND lease_token = {lease.Token} AND lease_until > clock_timestamp() AND status = 2
            """, token);
        if (stillOwned == 0) return new(null, false, true);
        await transaction.CommitAsync(token);
        return new(attempt.Id, false, false);
    }

    public Task<int> FinishAttemptAsync(Guid id, string outcome, CancellationToken token) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE learning_provider_attempts SET finished_at = clock_timestamp(), outcome = {outcome}
            WHERE id = {id} AND finished_at IS NULL
            """, token);

    public async Task<LearningRequestLease?> ClaimAsync(TimeSpan lease, CancellationToken token)
    {
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(lease));
        var leaseToken = Guid.NewGuid();
        // Claim only the unfinished head. A locked/backed-off/Running head still blocks
        // later requests. Claim takes just this row; apply takes user -> request -> targets.
        var rows = await db.Set<LearningRequest>().FromSqlInterpolated($"""
            UPDATE learning_requests SET status = 2, lease_token = {leaseToken},
                lease_until = clock_timestamp() + {lease}, attempt_count = attempt_count + 1
            WHERE id = (
                SELECT r.id FROM learning_requests r
                WHERE ((r.status = 1 AND r.available_at <= clock_timestamp())
                    OR (r.status = 2 AND r.lease_until <= clock_timestamp()))
                    AND NOT EXISTS (SELECT 1 FROM learning_requests earlier
                        WHERE earlier.user_id = r.user_id AND earlier.sequence < r.sequence AND earlier.status IN (1, 2))
                ORDER BY r.received_at, r.user_id, r.sequence
                FOR UPDATE OF r SKIP LOCKED LIMIT 1)
            RETURNING *
            """).AsNoTracking().ToListAsync(token);
        var request = rows.SingleOrDefault();
        return request is null ? null : new(request.Id, leaseToken, request.UserId, request.AttemptCount);
    }

    // Persist the prepared operation, marker, replies and completion atomically.
    public async Task<bool> ApplyAsync(LearningRequestLease lease,
        Func<LearningRequest, CancellationToken, Task<LearningRequestCompletion>> execute, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // Always user -> request -> targets, including competing command writers.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lease.UserId.Value})", token);
        var rows = await db.Set<LearningRequest>().FromSqlInterpolated($"""
            SELECT * FROM learning_requests r
            WHERE r.id = {lease.RequestId} AND r.user_id = {lease.UserId.Value}
                AND r.status = 2 AND r.lease_token = {lease.Token} AND r.lease_until > clock_timestamp()
                AND NOT EXISTS (SELECT 1 FROM learning_requests earlier
                    WHERE earlier.user_id = r.user_id AND earlier.sequence < r.sequence AND earlier.status IN (1, 2))
            FOR UPDATE OF r
            """).AsNoTracking().ToListAsync(token);
        var request = rows.SingleOrDefault();
        if (request is null || request.AttemptCount != lease.Attempt) return false;

        // The marker is a second durable guard, independent of receipt/source dedup.
        if (await db.Set<LearningAppliedOperation>().AnyAsync(operation => operation.RequestId == request.Id, token))
            throw new InvalidOperationException("An unfinished request already has an applied operation.");

        var execution = await execute(request, token);
        var replies = execution.Replies ??
            ["The learning request could not be processed. Retry the original command; no answer was assessed."];
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
        for (var ordinal = 0; ordinal < replies.Count; ordinal++)
        {
            var reply = replies[ordinal];
            db.TelegramOutboxMessages.Add(new TelegramOutboxMessage
            {
                Id = Guid.NewGuid(), ChatId = request.ChatId, LearningRequestId = request.Id, ReplyOrdinal = ordinal,
                Text = reply.Text, ReplyMarkupJson = reply.ReplyMarkupJson, EditMessageId = reply.EditMessageId,
                Status = OutboxMessageStatus.Pending, NextAttemptAt = now,
                CreatedAt = now.AddTicks(ordinal * 10L)
            });
        }
        db.Add(new LearningAppliedOperation { RequestId = request.Id, AppliedAt = now });
        await db.SaveChangesAsync(token);
        // Recheck the fence after target I/O/writes. An expired worker rolls back everything.
        var completed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE learning_requests SET status = 0, completed_at = clock_timestamp(), outcome = {execution.Outcome},
                dialogue_outcome = CAST({execution.DialogueOutcomeJson} AS jsonb),
                lease_token = NULL, lease_until = NULL
            WHERE id = {request.Id} AND status = 2 AND lease_token = {lease.Token} AND lease_until > clock_timestamp()
            """, token);
        if (completed == 0)
        {
            await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return false;
        }
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task<bool> SaveStepAsync(LearningRequestLease lease, LearningProviderStep step,
        TimeSpan renewal, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lease.UserId.Value})", token);
        var fenced = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE learning_requests SET lease_until = clock_timestamp() + {renewal}
            WHERE id = {lease.RequestId} AND user_id = {lease.UserId.Value} AND status = 2
                AND lease_token = {lease.Token} AND attempt_count = {lease.Attempt} AND lease_until > clock_timestamp()
            """, token);
        if (fenced == 0) return false;
        step.RequestId = lease.RequestId;
        var existing = await db.Set<LearningProviderStep>().SingleOrDefaultAsync(s => s.RequestId == lease.RequestId && s.Ordinal == step.Ordinal, token);
        if (existing is null) db.Add(step);
        else
        {
            if (existing.Kind != step.Kind || existing.Version != step.Version || existing.Fingerprint != step.Fingerprint || existing.TargetStamp != step.TargetStamp)
                throw new StaleLearningPreparationException();
            existing.Result = step.Result;
            existing.Failure = step.Failure;
        }
        await db.SaveChangesAsync(token);
        // Saving/checkpoint I/O may outlive the renewed lease. Fence the final
        // decision just as apply does, while still holding the request row lock.
        var stillOwned = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE learning_requests SET lease_token = lease_token
            WHERE id = {lease.RequestId} AND user_id = {lease.UserId.Value} AND status = 2
                AND lease_token = {lease.Token} AND attempt_count = {lease.Attempt} AND lease_until > clock_timestamp()
            """, token);
        if (stillOwned == 0)
        {
            await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return false;
        }
        await transaction.CommitAsync(token);
        return true;
    }
}
