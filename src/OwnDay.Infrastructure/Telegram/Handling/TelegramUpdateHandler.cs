using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using OwnDay.Application.Interactions;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Learning;
using OwnDay.Application.Dialogue;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Cleanup;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Delivery;
using OwnDay.Infrastructure.Telegram.Localization;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot.Types;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramUpdateHandler : ITelegramUpdateHandler
{
    private readonly TelegramLearningCommandHandler _learningCommands;
    private readonly TelegramStatisticsCommandHandler _statistics;
    private readonly TelegramActionCommandHandler _actionCommandHandler;
    private readonly TelegramStructuredCommandHandler _structuredCommandHandler;
    private readonly TelegramInboxFlow _inboxFlow;
    private readonly TelegramLanguageFlow _languageFlow;
    private readonly OwnDayDbContext _dbContext;
    private readonly TelegramUpdateRouter _router;
    private readonly TimeProvider _timeProvider;
    private readonly ITelegramMessageCleaner _messageCleaner;
    private readonly ILogger<TelegramUpdateHandler> _logger;
    private readonly ITelegramCallbackResponder? _callbackResponder;
    private readonly LearningRequestProcessingOptions _processing;

    public TelegramUpdateHandler(
        TelegramUpdateRouter router,
        TelegramActionCommandHandler actionCommandHandler,
        TelegramStructuredCommandHandler structuredCommandHandler,
        TelegramInboxFlow inboxFlow,
        TelegramLanguageFlow languageFlow,
        TelegramStatisticsCommandHandler statistics,
        OwnDayDbContext dbContext,
        TimeProvider timeProvider,
        ITelegramMessageCleaner messageCleaner,
        ILogger<TelegramUpdateHandler> logger,
        OwnDay.Application.Learning.LearningService learning,
        TelegramCurriculumCommandHandler curriculum,
        TelegramKnowledgeCommandHandler knowledge,
        TelegramStudyCommandHandler study,
        ITelegramCallbackResponder? callbackResponder = null,
        IOptions<LearningRequestProcessingOptions>? processing = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(actionCommandHandler);
        ArgumentNullException.ThrowIfNull(languageFlow);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(messageCleaner);
        ArgumentNullException.ThrowIfNull(logger);

        ArgumentNullException.ThrowIfNull(learning);
        _learningCommands = new(learning, curriculum, knowledge, study);
        _statistics = statistics;
        _router = router;
        _actionCommandHandler = actionCommandHandler;
        _structuredCommandHandler = structuredCommandHandler;
        _inboxFlow = inboxFlow;
        _languageFlow = languageFlow;
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _messageCleaner = messageCleaner;
        _logger = logger;
        _callbackResponder = callbackResponder;
        _processing = processing?.Value ?? new();
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
        var dialogue = await _dbContext.Set<DialogueContext>().SingleOrDefaultAsync(context => context.UserId == user, cancellationToken);
        if (dialogue is not null && (dialogue.SchemaVersion != DialogueContext.CurrentSchemaVersion || dialogue.Consultation is { Version: not 1 }))
            throw new InvalidOperationException("A compatible dialogue reader is required before accepting updates for this owner.");
        var diagnostic = await (from selected in _dbContext.Set<LearningContext>()
                                join program in _dbContext.Set<OwnDay.Domain.Learning.LearningProgram>() on selected.ProgramId equals program.Id
                                where selected.UserId == user && program.UserId == user
                                select program.EnglishDiagnostic).SingleOrDefaultAsync(cancellationToken);
        if (diagnostic is { Supported: false })
            throw new InvalidOperationException("A compatible English diagnostic reader is required before accepting updates for this owner.");
        var practice = await _dbContext.Set<LearningContext>().Where(context => context.UserId == user)
            .Select(context => context.Practice).SingleOrDefaultAsync(cancellationToken);
        if (practice is { Supported: false })
            throw new InvalidOperationException("A compatible practice reader is required before accepting updates for this owner.");
        var dialogueMode = _processing.DialogueEnabled || dialogue is not null ||
            await _dbContext.Set<LearningRequest>().AnyAsync(request => request.UserId == user &&
                (request.PayloadKind == "learning_text" || request.PayloadKind == "learning_command" && request.PayloadVersion == 3), cancellationToken);
        var introduceDialogue = false;

        async Task CommitReceiptAsync(DateTime acceptedAt)
        {
            await _dbContext.ProcessedTelegramUpdates.Where(processed => processed.UpdateId == update.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(processed => processed.ProcessedAt, acceptedAt), cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await AcknowledgeAsync(update, cancellationToken);
        }

        IReadOnlyList<string> replies;
        string? replyMarkup = null;
        long chatId;
        if (route is TelegramUpdateRouteResult.Text ordinary)
        {
            chatId = ordinary.ChatId;
            var activeLanguage = await _dbContext.TelegramUserLanguages.AnyAsync(row => row.UserId == user.Value && row.SelectionExpiresAt > now, cancellationToken);
            var activeInbox = await _dbContext.TelegramInboxDrafts.AnyAsync(row => row.UserId == user.Value && row.ExpiresAt > now, cancellationToken);
            if (dialogueMode && !activeLanguage && !activeInbox)
            {
                var databaseNow = await EnsureLearningAcceptanceAsync(cancellationToken);
                var lessonId = await CurrentLessonIdAsync(user, cancellationToken);
                await AddReceiptAsync(update, user, chatId, new LearningTextInput(ordinary.Value, lessonId,
                    dialogue?.PromptId, dialogue?.Revision, _processing.ConsultationEnabled || dialogue?.Consultation is not null,
                    _processing.EnglishDiagnosticEnabled, _processing.PracticeEnabled),
                    "learning_text", 1, databaseNow, ready: true, cancellationToken);
                await CommitReceiptAsync(databaseNow);
                return;
            }
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
            Guid? selectedProgramForHelp = null;
            if (dialogueMode && command.Name is "start" or "help" && (_processing.ConsultationEnabled || dialogue?.Consultation is not null))
                selectedProgramForHelp = await _dbContext.Set<LearningContext>().Where(context => context.UserId == user)
                    .Select(context => (Guid?)context.ProgramId).SingleOrDefaultAsync(cancellationToken);
            var dialogueCancel = dialogueMode && command.Name == "cancel" &&
                !await _dbContext.TelegramUserLanguages.AnyAsync(row => row.UserId == user.Value && row.SelectionExpiresAt > now, cancellationToken) &&
                !await _dbContext.TelegramInboxDrafts.AnyAsync(row => row.UserId == user.Value && row.ExpiresAt > now, cancellationToken);
            if (dialogueMode && command.Name is "start" or "help" && dialogue is null)
            {
                var active = await _dbContext.Set<Lesson>().SingleOrDefaultAsync(lesson => lesson.UserId == user &&
                    (lesson.Status == LessonStatus.AwaitingAnswer || lesson.Status == LessonStatus.Paused), cancellationToken);
                dialogue = DialogueContext.Create(user, active, new DateTimeOffset(await DatabaseNowAsync(cancellationToken)));
                _dbContext.Add(dialogue);
                introduceDialogue = true;
            }
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
            if (dialogueMode && command.Name is "capture" or "inbox" or "language" or "settings" or "back" && !dialogueCancel)
            {
                var interruptedAt = new DateTimeOffset(await DatabaseNowAsync(cancellationToken));
                if (dialogue is null)
                {
                    dialogue = DialogueContext.Create(user, null, interruptedAt);
                    _dbContext.Add(dialogue);
                    introduceDialogue = true;
                }
                dialogue.Interrupt(interruptedAt, update.Message?.Id ?? update.CallbackQuery?.Message?.Id ?? update.Id);
            }
            if (_learningCommands.Handles(command.Name) || dialogueCancel)
            {
                if (_processing.DurableIngressEnabled)
                {
                    var databaseNow = await EnsureLearningAcceptanceAsync(cancellationToken);
                    await AddLearningRequestAsync(update, dispatch, databaseNow, ready: true, cancellationToken, dialogueMode, dialogue);
                    await CommitReceiptAsync(databaseNow);
                    return;
                }
                if (dialogueMode) throw new InvalidOperationException("Keep the compatible dialogue producer/consumer installed for enrolled users.");
                // Closing the producer must never route around previously accepted work.
                if (await _dbContext.Set<LearningRequest>().AnyAsync(request => request.UserId == user &&
                    request.Status != LearningRequestStatus.Completed, cancellationToken))
                    throw new InvalidOperationException("Drain accepted Learning work before restoring synchronous commands.");
            }
            replies = command.Name switch
            {
                _ when _learningCommands.Handles(command.Name) => await _learningCommands.HandleAsync(command, cancellationToken),
                "start" or "help" when dialogueMode && command.Arguments.Trim() != "commands" =>
                    TelegramDialoguePresenter.Help(false, _processing.ConsultationEnabled || dialogue?.Consultation is not null, dialogue, selectedProgramForHelp),
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

        if (introduceDialogue) replies = [TelegramDialoguePresenter.Introduction, .. replies];

        // Synchronous compatibility boundary: no receipt is committed until the operation
        // and its reply intents complete. The existing user lock serializes sequences.
        LearningRequest? learningRequest = null;
        if (route is TelegramUpdateRouteResult.Dispatch learningDispatch && _learningCommands.Handles(learningDispatch.Command.Name))
        {
            learningRequest = await AddLearningRequestAsync(update, learningDispatch, now, ready: false, cancellationToken);
        }

        for (var index = 0; index < replies.Count; index++)
        {
            _dbContext.TelegramOutboxMessages.Add(new TelegramOutboxMessage
            {
                Id = Guid.NewGuid(),
                LearningRequestId = learningRequest?.Id,
                ReplyOrdinal = learningRequest is null ? null : index,
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
        else if (route is TelegramUpdateRouteResult.Dispatch && update.Message is { } message)
        {
            // Delete commands only after their result and reply are durable. Preserve free-text input.
            try
            {
                await _messageCleaner.DeleteMessageAsync(chatId, message.Id, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Could not delete processed Telegram command {MessageId} from chat {ChatId} ({ErrorType}).",
                    message.Id, chatId, exception.GetType().Name);
            }
        }
    }

    private async Task<DateTime> EnsureLearningAcceptanceAsync(CancellationToken token)
    {
        if (!_processing.DurableIngressEnabled || !_processing.Enabled || _processing.AcceptancePaused)
            throw new InvalidOperationException("Learning acceptance is temporarily paused.");
        var now = await DatabaseNowAsync(token);
        var oldestAllowed = now.AddSeconds(-_processing.MaxQueueAgeSeconds);
        if (await _dbContext.Set<LearningRequest>().AnyAsync(request => request.Status != LearningRequestStatus.Completed &&
            request.ReceivedAt < oldestAllowed, token)) throw new InvalidOperationException("Learning processing backlog requires recovery before accepting more work.");
        return now;
    }

    private Task<DateTime> DatabaseNowAsync(CancellationToken token) => _dbContext.Database.IsNpgsql()
        ? _dbContext.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token)
        : Task.FromResult(_timeProvider.GetUtcNow().UtcDateTime);

    private Task<Guid?> CurrentLessonIdAsync(UserId user, CancellationToken token) => _dbContext.Set<Lesson>()
        .Where(lesson => lesson.UserId == user && (lesson.Status == LessonStatus.AwaitingAnswer || lesson.Status == LessonStatus.Paused))
        .Select(lesson => (Guid?)lesson.Id).SingleOrDefaultAsync(token);

    private async Task<LearningRequest> AddLearningRequestAsync(Update update, TelegramUpdateRouteResult.Dispatch dispatch,
        DateTime receivedAt, bool ready, CancellationToken token, bool dialogueMode = false, DialogueContext? dialogue = null)
    {
        var command = dispatch.Command;
        Guid? lessonId = null;
        if (ready && (dialogueMode || command.Name is "answer" or "learn_ask" or "learn_explain"))
            lessonId = await CurrentLessonIdAsync(command.UserId, token);
        return await AddReceiptAsync(update, command.UserId, dispatch.ChatId,
            new LearningCommandInput(command.Name, command.Arguments, lessonId, dialogue?.PromptId, dialogue?.Revision),
            "learning_command", ready ? dialogueMode ? 3 : 2 : 1, receivedAt, ready, token);
    }

    private async Task<LearningRequest> AddReceiptAsync(Update update, UserId user, long chatId, object input,
        string kind, int version, DateTime receivedAt, bool ready, CancellationToken token)
    {
        var sequence = await _dbContext.Set<LearningRequest>().Where(request => request.UserId == user)
            .MaxAsync(request => (long?)request.Sequence, token) ?? 0;
        var request = new LearningRequest
        {
            UserId = user, Sequence = checked(sequence + 1),
            SourceKey = $"telegram:update:{update.Id}", SourceUpdateId = update.Id,
            SourceMessageId = update.Message?.Id ?? update.CallbackQuery?.Message?.Id,
            ChatId = chatId, PayloadKind = kind, PayloadVersion = version,
            NormalizedInput = JsonSerializer.Serialize(input, JsonSerializerOptions.Web),
            Status = ready ? LearningRequestStatus.Ready : LearningRequestStatus.Completed,
            Outcome = ready ? null : "reply_committed", ReceivedAt = receivedAt, AvailableAt = receivedAt,
            CompletedAt = ready ? null : _timeProvider.GetUtcNow().UtcDateTime
        };
        _dbContext.Add(request);
        return request;
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
