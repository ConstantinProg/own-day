using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnDay.Application.Inbox.Analysis;

namespace OwnDay.Infrastructure.OpenAIAnalysis;

public sealed class InboxAnalysisWorker(
    IServiceScopeFactory scopes,
    IOptions<InboxAnalysisProcessingOptions> processing,
    IOptions<OpenAIInboxAnalysisOptions> provider,
    TimeProvider clock,
    ILogger<InboxAnalysisWorker> logger) : BackgroundService
{
    internal static readonly Meter Meter = new("OwnDay.InboxAnalysis", "1.0");
    internal static readonly Counter<long> Claims = Meter.CreateCounter<long>("inbox_analysis.claims");
    internal static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("inbox_analysis.outcomes");
    private static readonly Counter<long> Errors = Meter.CreateCounter<long>("inbox_analysis.worker_errors");
    private static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>("inbox_analysis.active");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("inbox_analysis.duration", "s");

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = processing.Value;
        var ai = provider.Value;
        var canRun = options.Enabled && ai.Enabled && !string.IsNullOrWhiteSpace(ai.ApiKey);
        if (!canRun)
        {
            logger.LogInformation("Background Inbox analysis is paused: background/provider disabled or API key missing.");
        }

        return Task.WhenAll(Enumerable.Range(0, canRun ? options.Parallelism : 0)
            .Select(_ => RunAsync(options, stoppingToken)).Append(RecoverAsync(canRun, stoppingToken)));
    }

    private async Task RecoverAsync(bool canRun, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IInboxAnalysisJobStore>().RecoverAsync(canRun, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                Errors.Add(1);
                logger.LogWarning("Inbox analysis recovery failed ({ErrorType}).", exception.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromMinutes(1), clock, token);
        }
    }

    private async Task RunAsync(InboxAnalysisProcessingOptions options, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IInboxAnalysisJobStore>();
                var lease = await jobs.ClaimAsync(TimeSpan.FromSeconds(options.LeaseSeconds), token);
                if (lease is not null)
                {
                    Claims.Add(1);
                    Active.Add(1);
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        logger.LogInformation("Processing Inbox analysis job {JobId}, attempt {Attempt}.", lease.JobId, lease.Attempt);
                        var code = await scope.ServiceProvider.GetRequiredService<InboxAnalysisService>()
                            .ProcessJobAsync(lease, jobs, options, token);
                        Outcomes.Add(1, new KeyValuePair<string, object?>("code", code));
                        logger.LogInformation("Inbox analysis job {JobId}, attempt {Attempt}: {Code}.",
                            lease.JobId, lease.Attempt, code);
                    }
                    finally
                    {
                        Active.Add(-1);
                        Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
                    }

                    continue;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                Errors.Add(1);
                // Provider bodies, user text and connection strings never enter logs.
                logger.LogWarning("Inbox analysis cycle failed ({ErrorType}); lease will recover unfinished work.",
                    exception.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), clock, token);
        }
    }
}
