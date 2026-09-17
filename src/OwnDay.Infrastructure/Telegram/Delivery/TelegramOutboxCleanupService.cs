using Microsoft.EntityFrameworkCore;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.Telegram.Delivery;

public sealed class TelegramOutboxCleanupService(OwnDayDbContext dbContext, TimeProvider timeProvider)
{
    private const int BatchSize = 500;
    private const int RetentionDays = 30;
    private readonly OwnDayDbContext _dbContext = dbContext ??
        throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ??
        throw new ArgumentNullException(nameof(timeProvider));

    public async Task<BatchProcessingResult> DeleteExpiredBatchAsync(CancellationToken cancellationToken)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-RetentionDays);
        var deleted = await _dbContext.TelegramOutboxMessages
            .Where(message => message.Status == OutboxMessageStatus.Sent && message.SentAt < cutoff)
            .OrderBy(message => message.SentAt)
            .ThenBy(message => message.Id)
            .Take(BatchSize)
            .ExecuteDeleteAsync(cancellationToken);

        return new BatchProcessingResult(deleted == BatchSize);
    }
}
