using Microsoft.EntityFrameworkCore;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.Telegram.Delivery;

public sealed class TelegramOutboxCleanupService(OwnDayDbContext dbContext)
{
    private const int BatchSize = 500;
    private readonly OwnDayDbContext _dbContext = dbContext ??
        throw new ArgumentNullException(nameof(dbContext));

    public async Task<bool> DeleteExpiredAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var cutoff = utcNow.AddDays(-30);
        var deleted = await _dbContext.TelegramOutboxMessages
            .Where(message => message.Status == OutboxMessageStatus.Sent && message.SentAt < cutoff)
            .OrderBy(message => message.SentAt)
            .ThenBy(message => message.Id)
            .Take(BatchSize)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted == BatchSize;
    }
}
