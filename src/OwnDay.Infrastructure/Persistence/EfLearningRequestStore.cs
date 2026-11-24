using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning.Requests;
using OwnDay.Infrastructure.Learning;
using OwnDay.Infrastructure.Diagnostics;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfLearningRequestStore(OwnDayDbContext db, DiagnosticJournal? journal = null,
    DiagnosticText? diagnosticText = null) : ILearningRequestStore
{
    public sealed record AttemptAdmission(Guid? Id, bool Unknown, bool OwnershipLost)
    {
        public int? Number { get; init; }
    }

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
        if (attempts.Any(a => a.FinishedAt is null || a.Outcome is "unexpected_failure" or "cancelled" or "outcome_unknown")) return new(null, true, false);
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
        return new(attempt.Id, false, false) { Number = attempt.Number };
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
        return request is null ? null : new(request.Id, leaseToken, request.UserId, request.AttemptCount) { ReceivedAt = request.ReceivedAt };
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

        var started = Stopwatch.GetTimestamp();
        var changes = new List<DiagnosticStateChange>();
        if (journal is not null && DiagnosticJournal.Detailed && diagnosticText is not null)
        {
            db.ObserveDiagnosticChanges = context =>
            {
                try
                {
                    var captured = DiagnosticStateChanges.Capture(context, diagnosticText);
                    var remaining = Math.Max(0, 128 - changes.Count);
                    changes.AddRange(captured.Take(remaining));
                    if (captured.Length > remaining) journal.CountDropped(captured.Length - remaining);
                }
                catch (Exception) { journal.CountDropped(); }
            };
        }
        LearningRequestCompletion execution;
        try
        {
            execution = await execute(request, token);
            db.ObserveDiagnosticChanges?.Invoke(db);
        }
        catch (Exception error) when (error is not LearningProviderRequiredException and not StaleLearningPreparationException)
        {
            journal?.Write("persistence", "apply_aborted", new(Error: error.GetType().Name,
                DurationMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds), "error");
            throw;
        }
        finally
        {
            db.ObserveDiagnosticChanges = null;
        }
        journal?.Write("dialogue", "decision_proposed", new(Status: execution.Outcome,
            DurationMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds),
            detail: () => new { audit = diagnosticText?.Json(execution.DialogueOutcomeJson) });
        if (execution.Outcome.Contains("reject", StringComparison.Ordinal) || execution.Outcome is "stale_target" or "provider_outcome_unknown" or "unsupported_payload")
            journal?.Write("dialogue", "decision_rejected", new(Status: execution.Outcome), "warning");
        foreach (var change in changes)
            journal?.Write(change.Category, "state_change_proposed", new(Status: execution.Outcome, Kind: change.Entity),
                entity: change.Id, detail: () => new { source = "learning_apply", rule = execution.Outcome, change });

        var replies = execution.Replies ??
            ["The learning request could not be processed. Retry the original command; no answer was assessed."];
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
        var intents = new List<TelegramOutboxMessage>(replies.Count);
        for (var ordinal = 0; ordinal < replies.Count; ordinal++)
        {
            var reply = replies[ordinal];
            var intent = new TelegramOutboxMessage
            {
                Id = Guid.NewGuid(), ChatId = request.ChatId, LearningRequestId = request.Id, ReplyOrdinal = ordinal,
                Text = reply.Text, ReplyMarkupJson = reply.ReplyMarkupJson, EditMessageId = reply.EditMessageId,
                Status = OutboxMessageStatus.Pending, NextAttemptAt = now,
                CreatedAt = now.AddTicks(ordinal * 10L)
            };
            db.TelegramOutboxMessages.Add(intent);
            intents.Add(intent);
            WriteReply(intent, "reply_formed");
        }
        db.Add(new LearningAppliedOperation { RequestId = request.Id, AppliedAt = now });
        var saving = Stopwatch.GetTimestamp();
        try
        {
            await db.SaveChangesAsync(token);
            journal?.Write("persistence", "apply_writes_staged", new(DurationMs: (long)Stopwatch.GetElapsedTime(saving).TotalMilliseconds));
        }
        catch (Exception error)
        {
            journal?.Write("persistence", "apply_writes_failed", new(Error: error.GetType().Name,
                DurationMs: (long)Stopwatch.GetElapsedTime(saving).TotalMilliseconds), "error");
            throw;
        }
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
            journal?.Write("persistence", "state_change_rolled_back", new(Status: "lease_expired"), "warning");
            db.ChangeTracker.Clear();
            return false;
        }
        try
        {
            await transaction.CommitAsync(token);
        }
        catch (Exception error)
        {
            journal?.Write("persistence", "commit_outcome_unknown", new(Error: error.GetType().Name), "error");
            throw;
        }
        journal?.Write("learning", "request_completed", new(Status: execution.Outcome));
        journal?.Write("persistence", "transaction_committed", new(Status: execution.Outcome,
            DurationMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        foreach (var change in changes)
            journal?.Write(change.Category, "state_change_committed", new(Status: execution.Outcome, Kind: change.Entity),
                entity: change.Id, detail: () => new { source = "learning_apply", rule = execution.Outcome, change });
        foreach (var intent in intents) WriteReply(intent, "outbox_committed");
        return true;
    }

    private void WriteReply(TelegramOutboxMessage message, string code) =>
        journal?.Write("outbox", code, entity: message.Id, detail: () => new
        {
            text = diagnosticText?.Filter(message.Text),
            keyboard = diagnosticText?.Keyboard(message.ReplyMarkupJson)
        });

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
