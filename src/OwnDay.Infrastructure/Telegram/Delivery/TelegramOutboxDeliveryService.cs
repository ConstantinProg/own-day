using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox.Observability;
using Microsoft.Extensions.Logging;
using OwnDay.Infrastructure.Persistence;
using Telegram.Bot.Exceptions;

namespace OwnDay.Infrastructure.Telegram.Delivery;

public sealed class TelegramOutboxDeliveryService
{
    private const int BatchSize = 10;
    private const int MaximumRetryDelaySeconds = 3600;
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
                              message.NextAttemptAt <= now &&
                              !_dbContext.TelegramOutboxMessages.Any(pending =>
                                  pending.ChatId == message.ChatId && pending.Status == OutboxMessageStatus.Pending &&
                                  pending.NextAttemptAt > now &&
                                  (pending.CreatedAt < message.CreatedAt ||
                                   pending.CreatedAt == message.CreatedAt && pending.Id.CompareTo(message.Id) < 0)))
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var blockedChats = new HashSet<long>();
        foreach (var message in messages)
        {
            if (blockedChats.Contains(message.ChatId))
            {
                continue;
            }

            try
            {
                if (message.AttemptCount > 0)
                {
                    _dbContext.InboxOperationEvents.Add(new InboxOperationEvent
                    {
                        Code = InboxEventCodes.BackgroundDeliveryRetry, At = now
                    });
                }
                await _messageSender.SendMessageAsync(
                    message.ChatId,
                    message.Text,
                    message.ReplyMarkupJson,
                    cancellationToken);

                message.Status = OutboxMessageStatus.Sent;
                message.SentAt = _timeProvider.GetUtcNow().UtcDateTime;
                message.LastError = null;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (message.AttemptCount < int.MaxValue)
                {
                    message.AttemptCount++;
                }
                message.LastError = exception.GetType().Name;
                _dbContext.InboxOperationEvents.Add(new InboxOperationEvent
                {
                    Code = InboxEventCodes.BackgroundDeliveryError, At = _timeProvider.GetUtcNow().UtcDateTime
                });

                if (exception is ApiRequestException { ErrorCode: 400 or 403 or 404 })
                {
                    message.Status = OutboxMessageStatus.Failed;
                    _logger.LogError(
                        "Telegram outbox message {OutboxMessageId} was rejected permanently ({ErrorType}).",
                        message.Id, exception.GetType().Name);
                }
                else
                {
                    // Later messages in this chat must wait for this retry, including within this batch.
                    blockedChats.Add(message.ChatId);
                    message.NextAttemptAt = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(
                        Math.Min(MaximumRetryDelaySeconds, Math.Pow(2, Math.Min(message.AttemptCount, 12))));
                    _logger.LogWarning(
                        "Telegram outbox message {OutboxMessageId} delivery failed on attempt {AttemptCount} ({ErrorType}).",
                        message.Id,
                        message.AttemptCount, exception.GetType().Name);
                }
            }

            // Persist each attempt before a later send can be cancelled.
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // A full batch may have more ready messages behind it.
        return new BatchProcessingResult(messages.Count == BatchSize);
    }
}
