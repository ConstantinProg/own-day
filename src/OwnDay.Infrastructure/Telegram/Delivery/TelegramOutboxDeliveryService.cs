using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.Telegram.Delivery;

public sealed class TelegramOutboxDeliveryService
{
    private const int BatchSize = 10;
    private const int MaximumAttempts = 5;
    private readonly OwnDayDbContext _dbContext;
    private readonly ITelegramMessageSender _messageSender;
    private readonly ILogger<TelegramOutboxDeliveryService> _logger;
    private readonly TimeProvider _timeProvider;

    public TelegramOutboxDeliveryService(
        OwnDayDbContext dbContext,
        ITelegramMessageSender messageSender,
        ILogger<TelegramOutboxDeliveryService> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(messageSender);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContext = dbContext;
        _messageSender = messageSender;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<BatchProcessingResult> DeliverBatchAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var messages = await _dbContext.TelegramOutboxMessages
            .Where(message => message.Status == OutboxMessageStatus.Pending &&
                              message.NextAttemptAt <= now)
            .OrderBy(message => message.CreatedAt)
            .Take(BatchSize)
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
                message.SentAt = _timeProvider.GetUtcNow().UtcDateTime;
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
                    message.NextAttemptAt = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(
                        Math.Pow(2, message.AttemptCount));
                    _logger.LogWarning(
                        exception,
                        "Telegram outbox message {OutboxMessageId} delivery failed on attempt {AttemptCount}.",
                        message.Id,
                        message.AttemptCount);
                }
            }

            // Persist each attempt before a later send can be cancelled.
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // A full batch may have more ready messages behind it.
        return new BatchProcessingResult(messages.Count == BatchSize);
    }
}
