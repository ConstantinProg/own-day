using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using OwnDay.Application.Interactions;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Delivery;
using OwnDay.Infrastructure.Telegram.Localization;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot.Types;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramUpdateHandler : ITelegramUpdateHandler
{
    private readonly TelegramStatisticsCommandHandler _statistics;
    private readonly TelegramActionCommandHandler _actionCommandHandler;
    private readonly TelegramStructuredCommandHandler _structuredCommandHandler;
    private readonly TelegramInboxFlow _inboxFlow;
    private readonly TelegramLanguageFlow _languageFlow;
    private readonly OwnDayDbContext _dbContext;
    private readonly TelegramUpdateRouter _router;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TelegramUpdateHandler> _logger;
    private readonly ITelegramCallbackResponder? _callbackResponder;

    public TelegramUpdateHandler(
        TelegramUpdateRouter router,
        TelegramActionCommandHandler actionCommandHandler,
        TelegramStructuredCommandHandler structuredCommandHandler,
        TelegramInboxFlow inboxFlow,
        TelegramLanguageFlow languageFlow,
        TelegramStatisticsCommandHandler statistics,
        OwnDayDbContext dbContext,
        TimeProvider timeProvider,
        ILogger<TelegramUpdateHandler> logger,
        ITelegramCallbackResponder? callbackResponder = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(actionCommandHandler);
        ArgumentNullException.ThrowIfNull(languageFlow);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _statistics = statistics;
        _router = router;
        _actionCommandHandler = actionCommandHandler;
        _structuredCommandHandler = structuredCommandHandler;
        _inboxFlow = inboxFlow;
        _languageFlow = languageFlow;
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _logger = logger;
        _callbackResponder = callbackResponder;
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

        var statisticsRequest = route is TelegramUpdateRouteResult.Dispatch { Command.Name: "stats" };
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await HandleRoutedAsync(update, route, statisticsRequest, cancellationToken);
                return;
            }
            catch (Exception exception) when (statisticsRequest && attempt < 3 &&
                !cancellationToken.IsCancellationRequested && IsSerializationFailure(exception))
            {
                // Repeatable Read may reject a concurrent duplicate or language change. The failed
                // transaction has rolled back; retry deduplication and the entire report in a fresh snapshot.
                _dbContext.ChangeTracker.Clear();
            }
        }
    }

    private async Task HandleRoutedAsync(Update update, TelegramUpdateRouteResult route, bool statisticsRequest,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Statistics shares this connection and snapshot with update deduplication and its outbox reply.
        var isolation = statisticsRequest ? IsolationLevel.RepeatableRead : IsolationLevel.ReadCommitted;
        await using var transaction = _dbContext.Database.IsNpgsql()
            ? await _dbContext.Database.BeginTransactionAsync(isolation, cancellationToken)
            : await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var inserted = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO processed_telegram_updates (update_id, received_at)
            VALUES ({update.Id}, {now})
            ON CONFLICT (update_id) DO NOTHING
            """,
            cancellationToken);

        if (inserted is 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            await AcknowledgeAsync(update, cancellationToken);
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
        var sender = update.CallbackQuery?.From ?? update.Message!.From!;
        var locale = await _languageFlow.ResolveLocaleAsync(user, sender.LanguageCode, cancellationToken);

        IReadOnlyList<string> replies;
        string? replyMarkup = null;
        long chatId;
        if (route is TelegramUpdateRouteResult.Text ordinary)
        {
            chatId = ordinary.ChatId;
            var languageSelection = await _languageFlow.HasSelectionAsync(ordinary.UserId, cancellationToken);
            if (languageSelection)
            {
                // Also retire overlapping editors left by older deployments before consuming this input.
                await _inboxFlow.CloseAsync(ordinary.UserId, cancellationToken);
            }
            replies = await _languageFlow.ContinueAsync(ordinary.UserId, ordinary.Value, locale, cancellationToken) ??
                (languageSelection
                    ? await _inboxFlow.CaptureAsync(ordinary.UserId, ordinary.Value, locale, chatId, cancellationToken)
                    : await _inboxFlow.CaptureOrContinueAsync(ordinary.UserId, ordinary.Value, locale, cancellationToken, chatId));
        }
        else
        {
            var dispatch = (TelegramUpdateRouteResult.Dispatch)route;
            chatId = dispatch.ChatId;
            var command = dispatch.Command;
            var offeredAction = update.CallbackQuery?.Data is { } offeredData &&
                (offeredData.StartsWith("ia:", StringComparison.Ordinal) || offeredData.StartsWith("iu:", StringComparison.Ordinal));
            // Navigation ends input capture by a dialog. Context actions retain the manual draft.
            if (!offeredAction && command.Name is not ("language" or "cancel" or "back"))
            {
                await _languageFlow.CloseAsync(command.UserId, cancellationToken);
            }
            var contextAction = command.Name is "process" or "back" or "discard" or "cancel" or "capture" ||
                command.Name == "inbox" && command.Arguments.Length > 0 ||
                offeredAction;
            if (!contextAction)
            {
                await _inboxFlow.CloseAsync(command.UserId, cancellationToken);
            }
            replies = command.Name switch
            {
                "stats" => await _statistics.HandleAsync(command.UserId, command.Arguments, locale, cancellationToken),
                "settings" => [TelegramTexts.Get(locale, "settings.menu")],
                "language" => await _languageFlow.SelectAsync(command.UserId, command.Arguments.Trim(), locale, cancellationToken),
                "capture" => await _inboxFlow.CaptureAsync(command.UserId, command.Arguments, locale, chatId, cancellationToken),
                "process" => await _inboxFlow.ProcessAsync(command.UserId, command.Arguments, locale, cancellationToken),
                "back" => await BackAsync(command, locale, cancellationToken),
                "inbox" => await _inboxFlow.OpenAsync(command.UserId, command.Arguments, locale, cancellationToken),
                "inbox_undo" => await _inboxFlow.UndoAsync(command.UserId, command.Arguments, locale, cancellationToken),
                "inbox_result" => await _inboxFlow.ResultAsync(command.UserId, command.Arguments, locale, cancellationToken),
                "inbox_accept" or "inbox_reject" => await _inboxFlow.ResolveAsync(command.UserId, command.Arguments,
                    command.Name == "inbox_accept", locale, cancellationToken),
                "accept" or "reject" => await _inboxFlow.ResolveEntryAsync(command.UserId, command.Arguments,
                    command.Name == "accept", locale, cancellationToken),
                "cancel" => await CancelAsync(command, locale, cancellationToken),
                "discard" => await _inboxFlow.DiscardAsync(command.UserId, command.Arguments, locale, cancellationToken),
                _ when _actionCommandHandler.Handles(command.Name) => await _actionCommandHandler.HandleAsync(command, locale, cancellationToken),
                _ when _structuredCommandHandler.Handles(command.Name) => await _structuredCommandHandler.HandleAsync(command, locale, cancellationToken),
                _ => ServiceReply(command, locale)
            };
            replyMarkup = TelegramNavigation.Keyboard(command.Name, locale);
        }

        for (var index = 0; index < replies.Count; index++)
        {
            _dbContext.TelegramOutboxMessages.Add(new TelegramOutboxMessage
            {
                Id = Guid.NewGuid(),
                ChatId = chatId,
                Text = replies[index],
                ReplyMarkupJson = index == replies.Count - 1 ? replyMarkup : null,
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

        if (update.CallbackQuery is not null)
        {
            await AcknowledgeAsync(update, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<string>> CancelAsync(ProcessIncomingCommand command, string locale, CancellationToken token)
    {
        var language = await _languageFlow.CancelAsync(command.UserId, token);
        var inbox = await _inboxFlow.CloseAsync(command.UserId, token);
        return [TelegramTexts.Get(locale, language || inbox ? "selection.canceled" : "inbox.cancel.none")];
    }

    private async Task<IReadOnlyList<string>> BackAsync(ProcessIncomingCommand command, string locale, CancellationToken token)
    {
        if (await _languageFlow.CancelAsync(command.UserId, token))
        {
            return [TelegramTexts.Get(locale, "settings.menu")];
        }
        return await _inboxFlow.BackAsync(command.UserId, locale, token);
    }

    private static bool IsSerializationFailure(Exception exception) => exception is
        PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } or
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } };

    private async Task AcknowledgeAsync(Update update, CancellationToken token)
    {
        if (update.CallbackQuery is not { } callback || _callbackResponder is null)
        {
            return;
        }
        try
        {
            await _callbackResponder.AnswerAsync(callback.Id, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !token.IsCancellationRequested)
        {
            _logger.LogWarning("Could not acknowledge Telegram callback ({ErrorType}).", exception.GetType().Name);
        }
    }

    private static IReadOnlyList<string> ServiceReply(ProcessIncomingCommand command, string locale)
    {
        var key = command.Name switch
        {
            "start" => "legacy.start",
            "help" when command.Arguments.Trim() == "commands" => "help.commands",
            "help" => "legacy.help",
            "ping" => "legacy.ping",
            _ => "legacy.unknown"
        };
        return [TelegramTexts.Get(locale, key)];
    }
}
