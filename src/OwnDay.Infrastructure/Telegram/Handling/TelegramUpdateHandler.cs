using OwnDay.Application.Interactions;
using Microsoft.EntityFrameworkCore;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Delivery;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot.Types;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramUpdateHandler : ITelegramUpdateHandler
{
    private readonly IIncomingCommandHandler _commandHandler;
    private readonly OwnDayDbContext _dbContext;
    private readonly TelegramUpdateRouter _router;

    public TelegramUpdateHandler(
        TelegramUpdateRouter router,
        IIncomingCommandHandler commandHandler,
        OwnDayDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(commandHandler);
        ArgumentNullException.ThrowIfNull(dbContext);

        _router = router;
        _commandHandler = commandHandler;
        _dbContext = dbContext;
    }

    public async Task HandleAsync(
        Update update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var now = DateTime.UtcNow;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            cancellationToken);

        var inserted = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO processed_telegram_updates (update_id, received_at)
            VALUES ({update.Id}, {now})
            ON CONFLICT (update_id) DO NOTHING
            """,
            cancellationToken);

        if (inserted is 0)
        {
            return;
        }

        var result = _router.Route(update);

        if (result is TelegramUpdateRouteResult.Dispatch dispatch)
        {
            var commandResult = await _commandHandler.HandleAsync(
                dispatch.Command,
                cancellationToken);

            if (commandResult is IncomingCommandResult.Reply reply)
            {
                _dbContext.TelegramOutboxMessages.Add(new TelegramOutboxMessage
                {
                    Id = Guid.NewGuid(),
                    ChatId = update.Message!.Chat.Id,
                    Text = reply.Text,
                    Status = OutboxMessageStatus.Pending,
                    AttemptCount = 0,
                    NextAttemptAt = now,
                    CreatedAt = now
                });
            }
        }

        var processedUpdate = await _dbContext.ProcessedTelegramUpdates.SingleAsync(
            processed => processed.UpdateId == update.Id,
            cancellationToken);
        processedUpdate.ProcessedAt = now;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
