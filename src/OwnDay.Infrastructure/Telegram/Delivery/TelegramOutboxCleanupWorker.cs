using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OwnDay.Infrastructure.Telegram.Delivery;

public sealed class TelegramOutboxCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<TelegramOutboxCleanupWorker> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan BatchInterval = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _timeProvider = timeProvider ??
        throw new ArgumentNullException(nameof(timeProvider));
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ??
        throw new ArgumentNullException(nameof(scopeFactory));
    private readonly ILogger<TelegramOutboxCleanupWorker> _logger = logger ??
        throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = CleanupInterval;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var cleanup = scope.ServiceProvider.GetRequiredService<TelegramOutboxCleanupService>();
                var result = await cleanup.DeleteExpiredBatchAsync(stoppingToken);
                if (result.IsFullBatch)
                {
                    interval = BatchInterval;
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(exception, "Telegram outbox cleanup cycle failed.");
            }

            await Task.Delay(interval, _timeProvider, stoppingToken);
        }
    }
}
