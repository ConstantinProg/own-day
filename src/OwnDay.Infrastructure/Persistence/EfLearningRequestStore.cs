using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning.Requests;
using OwnDay.Infrastructure.Telegram.Commands;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfLearningRequestStore(OwnDayDbContext db) : ILearningRequestStore
{
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

    public async Task<bool> ApplyAsync(LearningRequestLease lease,
        Func<LearningRequest, CancellationToken, Task<LearningRequestExecution>> execute, CancellationToken token)
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
        var replies = execution.Replies ?? (execution.Result is null
            ? new[] { "The learning request could not be processed. Retry the original command; no answer was assessed." }
            : TelegramLearningPresenter.Render(execution.Result));
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
        for (var ordinal = 0; ordinal < replies.Count; ordinal++)
        {
            db.TelegramOutboxMessages.Add(new TelegramOutboxMessage
            {
                Id = Guid.NewGuid(), ChatId = request.ChatId, LearningRequestId = request.Id, ReplyOrdinal = ordinal,
                Text = replies[ordinal], Status = OutboxMessageStatus.Pending, NextAttemptAt = now,
                CreatedAt = now.AddTicks(ordinal * 10L)
            });
        }
        db.Add(new LearningAppliedOperation { RequestId = request.Id, AppliedAt = now });
        await db.SaveChangesAsync(token);
        // Recheck the fence after target I/O/writes. An expired worker rolls back everything.
        var completed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE learning_requests SET status = 0, completed_at = clock_timestamp(), outcome = {execution.Outcome},
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
