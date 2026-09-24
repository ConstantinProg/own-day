using OwnDay.Application.Interactions;
using Microsoft.EntityFrameworkCore;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot.Types;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramUpdateHandler : ITelegramUpdateHandler
{
    private readonly IIncomingCommandHandler _commandHandler;
    private readonly TelegramTaskCommandHandler _taskCommandHandler;
    private readonly OwnDayDbContext _dbContext;
    private readonly TelegramUpdateRouter _router;
    private readonly TimeProvider _timeProvider;

    public TelegramUpdateHandler(
        TelegramUpdateRouter router,
        IIncomingCommandHandler commandHandler,
        TelegramTaskCommandHandler taskCommandHandler,
        OwnDayDbContext dbContext,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(commandHandler);
        ArgumentNullException.ThrowIfNull(taskCommandHandler);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _router = router;
        _commandHandler = commandHandler;
        _taskCommandHandler = taskCommandHandler;
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task HandleAsync(
        Update update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();

        if (_router.Route(update) is not TelegramUpdateRouteResult.Dispatch dispatch)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

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

        IReadOnlyList<string> replies = _taskCommandHandler.Handles(dispatch.Command.Name)
            ? await _taskCommandHandler.HandleAsync(dispatch.Command, cancellationToken)
            : (await _commandHandler.HandleAsync(dispatch.Command, cancellationToken) is IncomingCommandResult.Reply reply
                ? [reply.Text]
                : []);

        for (var index = 0; index < replies.Count; index++)
        {
            _dbContext.TelegramOutboxMessages.Add(new TelegramOutboxMessage
            {
                Id = Guid.NewGuid(),
                ChatId = dispatch.ChatId,
                Text = replies[index],
                Status = OutboxMessageStatus.Pending,
                AttemptCount = 0,
                NextAttemptAt = now,
                CreatedAt = now.AddTicks(index * 10L)
            });
        }

        await _dbContext.ProcessedTelegramUpdates
            .Where(processed => processed.UpdateId == update.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(processed => processed.ProcessedAt, now),
                cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
