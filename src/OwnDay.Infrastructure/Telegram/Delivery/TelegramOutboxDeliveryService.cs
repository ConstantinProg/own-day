using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.Telegram.Delivery;

public sealed class TelegramOutboxDeliveryService
{
    private const int MaximumAttempts = 5;
    private readonly OwnDayDbContext _dbContext;
    private readonly ITelegramMessageSender _messageSender;
    private readonly ILogger<TelegramOutboxDeliveryService> _logger;

    public TelegramOutboxDeliveryService(
        OwnDayDbContext dbContext,
        ITelegramMessageSender messageSender,
        ILogger<TelegramOutboxDeliveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(messageSender);
        ArgumentNullException.ThrowIfNull(logger);

        _dbContext = dbContext;
        _messageSender = messageSender;
        _logger = logger;
    }

    public async Task DeliverPendingAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var messages = await _dbContext.TelegramOutboxMessages
            .Where(message => message.Status == OutboxMessageStatus.Pending &&
                              message.NextAttemptAt <= now)
            .OrderBy(message => message.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            try
            {
                await _messageSender.SendTextMessageAsync(
                    message.ChatId,
                    message.Text,
                    cancellationToken);

                message.Status = OutboxMessageStatus.Sent;
                message.SentAt = DateTime.UtcNow;
                message.LastError = null;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                message.AttemptCount++;
                message.LastError = exception.Message;

                if (message.AttemptCount >= MaximumAttempts)
                {
                    message.Status = OutboxMessageStatus.Failed;
                    _logger.LogError(
                        exception,
                        "Telegram outbox message {OutboxMessageId} exhausted delivery attempts.",
                        message.Id);
                }
                else
                {
                    message.NextAttemptAt = DateTime.UtcNow.AddSeconds(
                        Math.Pow(2, message.AttemptCount));
                    _logger.LogWarning(
                        exception,
                        "Telegram outbox message {OutboxMessageId} delivery failed on attempt {AttemptCount}.",
                        message.Id,
                        message.AttemptCount);
                }
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
