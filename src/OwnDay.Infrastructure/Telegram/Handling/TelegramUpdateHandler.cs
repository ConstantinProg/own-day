using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OwnDay.Application.Interactions;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Cleanup;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Localization;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot.Types;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramUpdateHandler : ITelegramUpdateHandler
{
    private readonly IIncomingCommandHandler _commandHandler;
    private readonly TelegramActionCommandHandler _actionCommandHandler;
    private readonly TelegramStructuredCommandHandler _structuredCommandHandler;
    private readonly TelegramInboxFlow _inboxFlow;
    private readonly TelegramLanguageFlow _languageFlow;
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
        TelegramLanguageFlow languageFlow,
        OwnDayDbContext dbContext,
        TimeProvider timeProvider,
        ITelegramMessageCleaner messageCleaner,
        ILogger<TelegramUpdateHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(commandHandler);
        ArgumentNullException.ThrowIfNull(actionCommandHandler);
        ArgumentNullException.ThrowIfNull(languageFlow);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(messageCleaner);
        ArgumentNullException.ThrowIfNull(logger);

        _router = router;
        _commandHandler = commandHandler;
        _actionCommandHandler = actionCommandHandler;
        _structuredCommandHandler = structuredCommandHandler;
        _inboxFlow = inboxFlow;
        _languageFlow = languageFlow;
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

        var user = route is TelegramUpdateRouteResult.Text textRoute
            ? textRoute.UserId
            : ((TelegramUpdateRouteResult.Dispatch)route).Command.UserId;
        var locale = await _languageFlow.ResolveLocaleAsync(user, update.Message!.From!.LanguageCode, cancellationToken);

        IReadOnlyList<string> replies;
        long chatId;
        if (route is TelegramUpdateRouteResult.Text ordinary)
        {
            chatId = ordinary.ChatId;
            replies = await _languageFlow.ContinueAsync(ordinary.UserId, ordinary.Value, locale, cancellationToken) ??
                await _inboxFlow.CaptureOrContinueAsync(ordinary.UserId, ordinary.Value, locale, cancellationToken);
        }
        else
        {
            var dispatch = (TelegramUpdateRouteResult.Dispatch)route;
            chatId = dispatch.ChatId;
            var command = dispatch.Command;
            if (command.Name is not "language" and not "cancel")
            {
                await _languageFlow.CloseAsync(command.UserId, cancellationToken);
            }
            replies = command.Name switch
            {
                "language" => await _languageFlow.OpenAsync(command.UserId, locale, cancellationToken),
                "inbox" => await _inboxFlow.OpenAsync(command.UserId, command.Arguments, locale, cancellationToken),
                "cancel" => await _languageFlow.CancelAsync(command.UserId, cancellationToken)
                    ? [TelegramTexts.Get(locale, "language.canceled")]
                    : await _inboxFlow.CancelAsync(command.UserId, locale, cancellationToken),
                "discard" => await _inboxFlow.DiscardAsync(command.UserId, command.Arguments, locale, cancellationToken),
                _ when _actionCommandHandler.Handles(command.Name) => await _actionCommandHandler.HandleAsync(command, locale, cancellationToken),
                _ when _structuredCommandHandler.Handles(command.Name) => await _structuredCommandHandler.HandleAsync(command, locale, cancellationToken),
                _ => await LegacyReplyAsync(command, locale, cancellationToken)
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

    private async Task<IReadOnlyList<string>> LegacyReplyAsync(ProcessIncomingCommand command, string locale, CancellationToken token)
    {
        var result = await _commandHandler.HandleAsync(command, token);
        if (result is not IncomingCommandResult.Reply)
        {
            return [];
        }

        var key = command.Name switch
        {
            "start" => "legacy.start",
            "help" => "legacy.help",
            "ping" => "legacy.ping",
            _ => "legacy.unknown"
        };
        return [TelegramTexts.Get(locale, key)];
    }
}
