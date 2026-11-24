using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning.Requests;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Diagnostics;

namespace OwnDay.Infrastructure.Learning;

public sealed class LearningRequestWorker(IServiceScopeFactory scopes, IOptions<LearningRequestProcessingOptions> options,
    TimeProvider clock, ILogger<LearningRequestWorker> logger, DiagnosticJournal? journal = null) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Enabled)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            if (!db.Database.IsNpgsql() || (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                throw new InvalidOperationException("Learning processing requires PostgreSQL with all compatible migrations applied before startup.");
        }
        await base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => options.Value.Enabled
        ? Task.WhenAll(Enumerable.Range(0, options.Value.Parallelism).Select(_ => RunAsync(stoppingToken)))
        : Task.CompletedTask;

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ILearningRequestStore>();
                var lease = await store.ClaimAsync(TimeSpan.FromSeconds(options.Value.LeaseSeconds), token);
                if (lease is not null)
                {
                    var capture = journal is null ? null :
                        await journal.CaptureAsync(lease.UserId, lease.RequestId, lease.RequestId, lease.Attempt, token);
                    using var diagnostics = DiagnosticJournal.Enter(capture is null ? null : capture with { WorkerStoppingToken = token });
                    var started = Stopwatch.GetTimestamp();
                    var waited = lease.ReceivedAt is { } received ? Math.Max(0, (long)(clock.GetUtcNow().UtcDateTime - received).TotalMilliseconds) : (long?)null;
                    journal?.Write("learning", "request_claimed", new(Status: "running", WaitMs: waited));
                    if (lease.Attempt > 1) journal?.Write("learning", "request_reclaimed", severity: "warning");
                    bool applied;
                    try
                    {
                        applied = await scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>()
                            .ProcessAsync(lease, options.Value.MaxAttempts, token);
                    }
                    catch (Exception error)
                    {
                        journal?.Write("learning", "request_attempt_failed", new(Error: error.GetType().Name,
                            DurationMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds), "error");
                        throw;
                    }
                    journal?.Write("learning", "request_attempt_finished", new(Status: applied ? "completed" : "ownership_lost",
                        DurationMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                    logger.LogInformation("Learning request {RequestId}, attempt {Attempt}: applied {Applied}.",
                        lease.RequestId, lease.Attempt, applied);
                    continue;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                // Leave the lease durable. Restart/reclaim retries it within the attempt budget.
                logger.LogWarning("Learning request cycle failed ({ErrorType}).", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(options.Value.PollMilliseconds), clock, token);
        }
    }
}
