using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnDay.Application.Inbox;
using OwnDay.Application.Interactions;
using OwnDay.Application.Learning.Requests;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Learning;
using OwnDay.Application.Dialogue;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Actions;
using OwnDay.Infrastructure.Telegram.Cleanup;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Delivery;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot.Types;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramUpdateHandler : ITelegramUpdateHandler
{
    private readonly TelegramLearningCommandHandler _learningCommands;
    private readonly IInboxDraftStore _inboxDrafts;
    private readonly TelegramLanguageFlow _languageFlow;
    private readonly OwnDayDbContext _dbContext;
    private readonly TelegramUpdateRouter _router;
    private readonly TimeProvider _timeProvider;
    private readonly ITelegramMessageCleaner _messageCleaner;
    private readonly ILogger<TelegramUpdateHandler> _logger;
    private readonly ApplicationVersion _applicationVersion;
    private readonly ITelegramCallbackResponder? _callbackResponder;
    private readonly LearningRequestProcessingOptions _processing;
    private readonly TelegramActionKeyboard? _keyboard;

    public TelegramUpdateHandler(
        TelegramUpdateRouter router,
        IInboxDraftStore inboxDrafts,
        TelegramLanguageFlow languageFlow,
        OwnDayDbContext dbContext,
        TimeProvider timeProvider,
        ITelegramMessageCleaner messageCleaner,
        ILogger<TelegramUpdateHandler> logger,
        OwnDay.Application.Learning.LearningService learning,
        TelegramCurriculumCommandHandler curriculum,
        TelegramKnowledgeCommandHandler knowledge,
        TelegramStudyCommandHandler study,
        ApplicationVersion applicationVersion,
        ITelegramCallbackResponder? callbackResponder = null,
        IOptions<LearningRequestProcessingOptions>? processing = null,
        TelegramActionKeyboard? keyboard = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(languageFlow);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(messageCleaner);
        ArgumentNullException.ThrowIfNull(logger);

        ArgumentNullException.ThrowIfNull(learning);
        _learningCommands = new(learning, curriculum, knowledge, study);
        _router = router;
        _inboxDrafts = inboxDrafts;
        _languageFlow = languageFlow;
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _messageCleaner = messageCleaner;
        _logger = logger;
        _applicationVersion = applicationVersion;
        _callbackResponder = callbackResponder;
        _processing = processing?.Value ?? new();
        _keyboard = keyboard;
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

        if (route is TelegramUpdateRouteResult.UnavailableAction unavailable)
        {
            await AcknowledgeAsync(update, cancellationToken, unavailable.InRussian
                ? "Действие недоступно. Откройте последнее сообщение или напишите «Продолжим»."
                : "Action unavailable. Use the latest message or write “continue”.");
            return;
        }

        try
        {
            await HandleRoutedAsync(update, route, cancellationToken);
        }
        catch (LearningAcceptanceUnavailableException) when (route is TelegramUpdateRouteResult.Action)
        {
            var russian = update.CallbackQuery?.From.LanguageCode?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) == true;
            await AcknowledgeAsync(update, cancellationToken, russian
                ? "Сейчас не удалось принять действие. Попробуйте чуть позже."
                : "The action could not be accepted right now. Try again shortly.");
            throw;
        }
    }

    private async Task HandleRoutedAsync(Update update, TelegramUpdateRouteResult route, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = _dbContext.Database.IsNpgsql()
            ? await _dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
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

        if (route is TelegramUpdateRouteResult.Dispatch { Command.Name: "version" } version)
        {
            await CommitRepliesAsync(update, version.ChatId, [$"OwnDay {_applicationVersion.Version}"],
                now, transaction, cancellationToken);
            return;
        }

        var user = route switch
        {
            TelegramUpdateRouteResult.Action action => action.UserId,
            TelegramUpdateRouteResult.Text text => text.UserId,
            TelegramUpdateRouteResult.Dispatch dispatch => dispatch.Command.UserId,
            _ => throw new InvalidOperationException("Unexpected route.")
        };
        if (_dbContext.Database.IsNpgsql())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({user.Value})", cancellationToken);
        }

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
        var introduceDialogue = false;

        async Task CommitReceiptAsync(DateTime acceptedAt)
        {
            await _dbContext.ProcessedTelegramUpdates.Where(processed => processed.UpdateId == update.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(processed => processed.ProcessedAt, acceptedAt), cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await AcknowledgeAsync(update, cancellationToken);
        }

        if (route is TelegramUpdateRouteResult.Action contextual)
        {
            var databaseNow = await EnsureLearningAcceptanceAsync(cancellationToken);
            await AddReceiptAsync(update, user, contextual.ChatId,
                new LearningActionInput(contextual.Value, contextual.ExpiresAt,
                    update.CallbackQuery?.From.LanguageCode?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) == true,
                    _processing.ProgramSwitchEnabled),
                "learning_action", 1, databaseNow, ready: true, cancellationToken);
            await CommitReceiptAsync(databaseNow);
            return;
        }

        IReadOnlyList<TelegramLearningReply> replies;
        long chatId;
        if (route is TelegramUpdateRouteResult.Text ordinary)
        {
            chatId = ordinary.ChatId;
            // Retire legacy editors before accepting text into the sole dialogue lane.
            await CloseLegacyInboxDraftAsync(user, cancellationToken);
            await _languageFlow.CloseAsync(user, cancellationToken);
            var databaseNow = await EnsureLearningAcceptanceAsync(cancellationToken);
            var lessonId = await CurrentLessonIdAsync(user, cancellationToken);
            if (TelegramStudyCommandHandler.ParseText(ordinary.Value, user) is { } study)
            {
                await AddReceiptAsync(update, user, chatId,
                    new LearningCommandInput(study.Name, study.Arguments, lessonId, dialogue?.PromptId, dialogue?.Revision),
                    "learning_command", 3, databaseNow, ready: true, cancellationToken);
            }
            else
            {
                await AddReceiptAsync(update, user, chatId, new LearningTextInput(ordinary.Value, lessonId,
                    dialogue?.PromptId, dialogue?.Revision, true,
                    _processing.EnglishDiagnosticEnabled, _processing.PracticeEnabled, _processing.ProgramSwitchEnabled),
                    "learning_text", 1, databaseNow, ready: true, cancellationToken);
            }
            await CommitReceiptAsync(databaseNow);
            return;
        }
        else
        {
            var dispatch = (TelegramUpdateRouteResult.Dispatch)route;
            chatId = dispatch.ChatId;
            var command = dispatch.Command;
            await CloseLegacyInboxDraftAsync(user, cancellationToken);
            await _languageFlow.CloseAsync(user, cancellationToken);
            if (_learningCommands.Handles(command.Name) || command.Name == "cancel")
            {
                var databaseNow = await EnsureLearningAcceptanceAsync(cancellationToken);
                await AddLearningRequestAsync(update, dispatch, databaseNow, ready: true, cancellationToken,
                    dialogueMode: true, dialogue: dialogue);
                await CommitReceiptAsync(databaseNow);
                return;
            }
            var selectedProgramForHelp = await _dbContext.Set<LearningContext>().Where(context => context.UserId == user)
                .Select(context => (Guid?)context.ProgramId).SingleOrDefaultAsync(cancellationToken);
            if (dialogue is null)
            {
                var active = await _dbContext.Set<Lesson>().SingleOrDefaultAsync(lesson => lesson.UserId == user &&
                    (lesson.Status == LessonStatus.AwaitingAnswer || lesson.Status == LessonStatus.Paused), cancellationToken);
                dialogue = DialogueContext.Create(user, active, new DateTimeOffset(await DatabaseNowAsync(cancellationToken)));
                _dbContext.Add(dialogue);
                introduceDialogue = true;
            }
            // Old commands and buttons explain the current dialogue without executing legacy actions.
            var help = TelegramDialoguePresenter.Help(false, true, dialogue, selectedProgramForHelp, showVersionCommand: command.Name == "help");
            replies = [.. help.Select(text => new TelegramLearningReply(text))];
            if (command.Name is "start" or "help" && help.Count > 0 &&
                (selectedProgramForHelp is not null || dialogue.TargetLessonId is not null || dialogue.Consultation is not null))
            {
                var button = TelegramLearningReply.WithActions(help[^1], [new(LearningActionKind.Continue)], inRussian: true);
                replies = [.. replies.SkipLast(1), _keyboard?.Render(button, user.Value, chatId, new(now), dialogue) ?? button];
            }
        }

        if (introduceDialogue) replies = [TelegramDialoguePresenter.Introduction, .. replies];

        await CommitRepliesAsync(update, chatId, replies, now, transaction, cancellationToken);
    }

    private async Task CommitRepliesAsync(Update update, long chatId, IReadOnlyList<TelegramLearningReply> replies, DateTime now,
        IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        for (var index = 0; index < replies.Count; index++)
        {
            _dbContext.TelegramOutboxMessages.Add(new TelegramOutboxMessage
            {
                Id = Guid.NewGuid(),
                ChatId = chatId,
                Text = replies[index].Text,
                ReplyMarkupJson = replies[index].ReplyMarkupJson,
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
        else if (update.Message is { } message)
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

    private async Task CloseLegacyInboxDraftAsync(UserId user, CancellationToken token)
    {
        if (await _inboxDrafts.FindAsync(user, token) is { } draft)
            _inboxDrafts.Remove(draft);
    }

    private async Task<DateTime> EnsureLearningAcceptanceAsync(CancellationToken token)
    {
        if (!_processing.DurableIngressEnabled || !_processing.Enabled || _processing.AcceptancePaused)
            throw new LearningAcceptanceUnavailableException("Learning acceptance is temporarily paused.");
        var now = await DatabaseNowAsync(token);
        var oldestAllowed = now.AddSeconds(-_processing.MaxQueueAgeSeconds);
        if (await _dbContext.Set<LearningRequest>().AnyAsync(request => request.Status != LearningRequestStatus.Completed &&
            request.ReceivedAt < oldestAllowed, token))
            throw new LearningAcceptanceUnavailableException("Learning processing backlog requires recovery before accepting more work.");
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
            new LearningCommandInput(command.Name, command.Arguments, lessonId, dialogue?.PromptId, dialogue?.Revision, _processing.ProgramSwitchEnabled),
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

    private async Task AcknowledgeAsync(Update update, CancellationToken token, string? notice = null)
    {
        if (update.CallbackQuery is not { } callback || _callbackResponder is null)
        {
            return;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            if (notice is null) await _callbackResponder.AnswerAsync(callback.Id, timeout.Token);
            else await _callbackResponder.AnswerAsync(callback.Id, notice, timeout.Token);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            _logger.LogWarning("Could not acknowledge Telegram callback ({ErrorType}).", exception.GetType().Name);
        }
    }

    private sealed class LearningAcceptanceUnavailableException(string message) : InvalidOperationException(message);
}
