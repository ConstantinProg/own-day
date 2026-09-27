using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OwnDay.Application.Interactions;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Cleanup;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot.Types;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramUpdateHandler : ITelegramUpdateHandler
{
    private readonly IIncomingCommandHandler _commandHandler;
    private readonly TelegramActionCommandHandler _actionCommandHandler;
    private readonly TelegramStructuredCommandHandler _structuredCommandHandler;
    private readonly TelegramInboxFlow _inboxFlow;
    private readonly OwnDayDbContext _dbContext;
    private readonly TelegramUpdateRouter _router;
    private readonly TimeProvider _timeProvider;
    private readonly ITelegramMessageCleaner _messageCleaner;
    private readonly ILogger<TelegramUpdateHandler> _logger;

    public TelegramUpdateHandler(
        TelegramUpdateRouter router,
        IIncomingCommandHandler commandHandler,
        TelegramActionCommandHandler actionCommandHandler,
        TelegramStructuredCommandHandler structuredCommandHandler,
        TelegramInboxFlow inboxFlow,
        OwnDayDbContext dbContext,
        TimeProvider timeProvider,
        ITelegramMessageCleaner messageCleaner,
        ILogger<TelegramUpdateHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(commandHandler);
        ArgumentNullException.ThrowIfNull(actionCommandHandler);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(messageCleaner);
        ArgumentNullException.ThrowIfNull(logger);

        _router = router;
        _commandHandler = commandHandler;
        _actionCommandHandler = actionCommandHandler;
        _structuredCommandHandler = structuredCommandHandler;
        _inboxFlow = inboxFlow;
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _messageCleaner = messageCleaner;
        _logger = logger;
    }

    public async Task HandleAsync(
        Update update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();

        var route = _router.Route(update);
        if (route is TelegramUpdateRouteResult.Ignore)
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

        var userId = route is TelegramUpdateRouteResult.Text routedText
            ? routedText.UserId.Value
            : ((TelegramUpdateRouteResult.Dispatch)route).Command.UserId.Value;
        if (_dbContext.Database.IsNpgsql())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({userId})", cancellationToken);
        }

        IReadOnlyList<string> replies;
        long chatId;
        if (route is TelegramUpdateRouteResult.Text ordinary)
        {
            chatId = ordinary.ChatId;
            replies = await _inboxFlow.CaptureOrContinueAsync(ordinary.UserId, ordinary.Value, cancellationToken);
        }
        else
        {
            var dispatch = (TelegramUpdateRouteResult.Dispatch)route;
            chatId = dispatch.ChatId;
            var command = dispatch.Command;
            replies = command.Name switch
            {
                "inbox" => await _inboxFlow.OpenAsync(command.UserId, command.Arguments, cancellationToken),
                "cancel" => await _inboxFlow.CancelAsync(command.UserId, cancellationToken),
                "discard" => await _inboxFlow.DiscardAsync(command.UserId, command.Arguments, cancellationToken),
                _ when _actionCommandHandler.Handles(command.Name) => await _actionCommandHandler.HandleAsync(command, cancellationToken),
                _ when _structuredCommandHandler.Handles(command.Name) => await _structuredCommandHandler.HandleAsync(command, cancellationToken),
                _ => await LegacyReplyAsync(command, cancellationToken)
            };
        }

        for (var index = 0; index < replies.Count; index++)
        {
            _dbContext.TelegramOutboxMessages.Add(new TelegramOutboxMessage
            {
                Id = Guid.NewGuid(),
                ChatId = chatId,
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

        try
        {
            await _messageCleaner.DeleteMessageAsync(chatId, update.Message!.Id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception,
                "Could not delete processed Telegram message {MessageId} from chat {ChatId}.",
                update.Message!.Id, chatId);
        }
    }

    private async Task<IReadOnlyList<string>> LegacyReplyAsync(ProcessIncomingCommand command, CancellationToken token) =>
        await _commandHandler.HandleAsync(command, token) is IncomingCommandResult.Reply reply ? [reply.Text] : [];
}
