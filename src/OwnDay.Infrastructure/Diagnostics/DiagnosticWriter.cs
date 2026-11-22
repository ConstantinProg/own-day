using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.Diagnostics;

internal sealed class DiagnosticWriter(DiagnosticJournal journal, IServiceScopeFactory scopes,
    IOptions<DiagnosticOptions> options, ILogger<DiagnosticWriter> logger, TimeProvider clock) : BackgroundService
{
    private readonly CancellationTokenSource _abort = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stopping = stoppingToken.Register(journal.Complete);
        var token = _abort.Token;
        var nextCleanup = DateTimeOffset.MinValue;
        try
        {
            while (true)
            {
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(token);
                poll.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    if (!await journal.Reader.WaitToReadAsync(poll.Token)) break;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }

                var batch = new List<DiagnosticEvent>(options.Value.BatchSize);
                while (batch.Count < options.Value.BatchSize && journal.Reader.TryRead(out var item))
                    batch.Add(item);
                foreach (var group in batch.GroupBy(row => row.OwnerId))
                    await WriteWithRetryAsync(group.Key, group.ToArray(), token);

                if (!stoppingToken.IsCancellationRequested && nextCleanup <= clock.GetUtcNow())
                {
                    await CleanupAsync(token);
                    nextCleanup = clock.GetUtcNow().AddMinutes(5);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            while (journal.Reader.TryRead(out var item)) journal.CountDropped(owner: item.OwnerId, generation: item.Generation);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        journal.Complete();
        _abort.CancelAfter(TimeSpan.FromSeconds(3));
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _abort.Cancel();
        base.Dispose();
        _abort.Dispose();
    }

    private async Task WriteWithRetryAsync(long owner, DiagnosticEvent[] rows, CancellationToken token)
    {
        try
        {
            for (var retry = 0; retry < 4; retry++)
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    if (await WriteAsync(owner, rows, deadline.Token)) return;
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    if (retry == 3)
                        logger.LogWarning("Diagnostic batch could not be stored ({ErrorType}).", error.GetType().Name);
                }
                if (retry < 3) await Task.Delay(TimeSpan.FromMilliseconds(100 << retry), clock, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            CountBatchLoss();
            throw;
        }
        CountBatchLoss();

        void CountBatchLoss()
        {
            foreach (var group in rows.GroupBy(row => row.Generation))
                journal.CountDropped(group.Count(), owner, group.Key);
        }
    }

    internal async Task<bool> WriteAsync(long owner, DiagnosticEvent[] rows, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        if (!db.Database.IsNpgsql()) return true;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({DiagnosticFencing.LockNamespace}, {owner.GetHashCode()}) AS \"Value\"").SingleAsync(token))
            return false;
        var fence = await db.Set<DiagnosticFence>().AsNoTracking().SingleOrDefaultAsync(row => row.OwnerId == owner, token);
        var generation = fence?.Generation ?? Guid.Empty;
        var detailGeneration = fence?.DetailGeneration ?? Guid.Empty;
        var accepted = rows.Where(row => row.Generation == generation && row.OccurredAt >= clock.GetUtcNow().UtcDateTime.AddDays(-DiagnosticOptions.RetentionDays))
            .Select(row => row.Detail == "tester" && row.DetailGeneration != detailGeneration ? RemoveDetail(row) : row);
        var encoded = JsonSerializer.Serialize(accepted);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO diagnostic_events ("Id", "OwnerId", "Generation", "OccurredAt", "Category", "Code", "Severity",
                "CorrelationId", "RequestId", "AttemptNumber", "EntityId", "SchemaVersion", "Detail", "Data", "DetailGeneration")
            SELECT "Id", "OwnerId", "Generation", "OccurredAt", "Category", "Code", "Severity",
                "CorrelationId", "RequestId", "AttemptNumber", "EntityId", "SchemaVersion", "Detail", "Data"::jsonb, "DetailGeneration"
            FROM jsonb_to_recordset({encoded}::jsonb) AS r("Id" uuid, "OwnerId" bigint, "Generation" uuid,
                "OccurredAt" timestamptz, "Category" text, "Code" text, "Severity" text, "CorrelationId" uuid,
                "RequestId" uuid, "AttemptNumber" integer, "EntityId" uuid, "SchemaVersion" integer, "Detail" text, "Data" text, "DetailGeneration" uuid)
            ON CONFLICT ("Id") DO NOTHING
            """, token);
        await transaction.CommitAsync(token);
        return true;
    }

    private static DiagnosticEvent RemoveDetail(DiagnosticEvent row)
    {
        using var json = JsonDocument.Parse(row.Data);
        return row with { Detail = "minimal", Data = JsonSerializer.Serialize(new
        {
            fields = json.RootElement.GetProperty("fields"),
            detailOmitted = "tester_access_revoked"
        }) };
    }

    private async Task CleanupAsync(CancellationToken token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            if (!db.Database.IsNpgsql()) return;
            var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-DiagnosticOptions.RetentionDays);
            await db.Set<DiagnosticEvent>().Where(row => row.OccurredAt < cutoff)
                .OrderBy(row => row.OccurredAt).ThenBy(row => row.Id).Take(1000).ExecuteDeleteAsync(deadline.Token);
            await db.ProcessedTelegramUpdates.Where(row => row.DiagnosticOwnerId != null && row.ReceivedAt < cutoff)
                .OrderBy(row => row.ReceivedAt).ThenBy(row => row.UpdateId).Take(1000).ExecuteUpdateAsync(setters =>
                    setters.SetProperty(row => row.DiagnosticOwnerId, (long?)null)
                        .SetProperty(row => row.DiagnosticCorrelationId, (Guid?)null), deadline.Token);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            logger.LogWarning("Diagnostic retention cycle failed ({ErrorType}).", error.GetType().Name);
        }
    }
}
