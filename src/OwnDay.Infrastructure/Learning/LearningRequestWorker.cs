using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnDay.Application.Learning.Requests;

namespace OwnDay.Infrastructure.Learning;

public sealed class LearningRequestWorker(IServiceScopeFactory scopes, IOptions<LearningRequestProcessingOptions> options,
    TimeProvider clock, ILogger<LearningRequestWorker> logger) : BackgroundService
{
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
                    var applied = await scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>()
                        .ProcessAsync(lease, options.Value.MaxAttempts, token);
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
