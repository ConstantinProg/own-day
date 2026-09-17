using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OwnDay.Infrastructure.Telegram.Delivery;

public sealed class TelegramOutboxWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<TelegramOutboxWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ??
        throw new ArgumentNullException(nameof(scopeFactory));
    private readonly ILogger<TelegramOutboxWorker> _logger = logger ??
        throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var deliveryService = scope.ServiceProvider
                    .GetRequiredService<TelegramOutboxDeliveryService>();

                await deliveryService.DeliverPendingAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(exception, "Telegram outbox delivery cycle failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
