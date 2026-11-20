using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Application.Inbox.Observability;
using Microsoft.Extensions.Logging;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
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
        var persistedOrder = _dbContext.Database.IsNpgsql();
        var query = _dbContext.TelegramOutboxMessages
            .Where(message => message.Status == OutboxMessageStatus.Pending &&
                              message.NextAttemptAt <= now &&
                              !_dbContext.TelegramOutboxMessages.Any(pending =>
                                  pending.ChatId == message.ChatId && pending.Status == OutboxMessageStatus.Pending &&
                                  pending.NextAttemptAt > now &&
                                  (persistedOrder ? pending.DeliveryOrder < message.DeliveryOrder :
                                      pending.CreatedAt < message.CreatedAt ||
                                      pending.CreatedAt == message.CreatedAt && pending.Id.CompareTo(message.Id) < 0)));
        var ordered = persistedOrder ? query.OrderBy(message => message.DeliveryOrder)
            : query.OrderBy(message => message.CreatedAt).ThenBy(message => message.Id);
        var messages = await ordered
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var blockedChats = new HashSet<long>();
        foreach (var message in messages)
        {
            if (blockedChats.Contains(message.ChatId))
            {
                continue;
            }

            long? owner = message.StudyInvitationId is not null ? message.ChatId : null;
            if (persistedOrder && message.LearningRequestId is { } requestId)
            {
                var requestOwner = await _dbContext.Set<LearningRequest>().AsNoTracking()
                    .Where(request => request.Id == requestId).Select(request => (UserId?)request.UserId)
                    .SingleOrDefaultAsync(cancellationToken);
                if (requestOwner is null) continue;
                owner = requestOwner.Value.Value;
            }

            await using var ownerLock = persistedOrder && owner is { } ownerId
                ? await OwnerLock.AcquireAsync(_dbContext, ownerId, cancellationToken) : null;
            if (owner is not null)
            {
                var entry = _dbContext.Entry(message);
                await entry.ReloadAsync(cancellationToken);
                if (entry.State == EntityState.Detached || message.Status != OutboxMessageStatus.Pending)
                {
                    continue;
                }
                if (message.NextAttemptAt > _timeProvider.GetUtcNow().UtcDateTime)
                {
                    blockedChats.Add(message.ChatId);
                    continue;
                }
            }
            if (message.StudyInvitationId is { } invitation)
            {
                var user = new UserId(message.ChatId);
                var schedule = await _dbContext.Set<StudySchedule>().AsNoTracking().SingleOrDefaultAsync(
                    s => s.UserId == user, cancellationToken);
                var active = await _dbContext.Set<Lesson>().AsNoTracking().SingleOrDefaultAsync(lesson => lesson.UserId == user &&
                    (lesson.Status == LessonStatus.AwaitingAnswer || lesson.Status == LessonStatus.Paused), cancellationToken);
                var review = schedule?.SkillId is { } skillId
                    ? await _dbContext.Set<ReviewSchedule>().AsNoTracking().SingleOrDefaultAsync(
                        r => r.UserId == user && r.SkillId == skillId, cancellationToken) : null;
                var selected = schedule?.ProgramId is not null
                    ? await _dbContext.Set<LearningContext>().AsNoTracking().Where(context => context.UserId == user)
                        .Select(context => (Guid?)context.ProgramId).SingleOrDefaultAsync(cancellationToken) : null;
                if (!StudyService.CanDeliver(schedule, invitation, _timeProvider.GetUtcNow(), active, review, selected))
                {
                    message.Status = OutboxMessageStatus.Failed;
                    message.LastError = "StudyInvitationSuppressed";
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    continue;
                }
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
                if (message.EditMessageId is { } target)
                    await _messageSender.EditKeyboardAsync(message.ChatId, target, message.ReplyMarkupJson, cancellationToken);
                else
                    await _messageSender.SendMessageAsync(message.ChatId, message.Text, message.ReplyMarkupJson, cancellationToken);
                MarkSent(message);
            }
            catch (ApiRequestException exception) when (message.EditMessageId is not null && exception.ErrorCode == 400 &&
                exception.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
            {
                MarkSent(message);
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

                if (exception is ApiRequestException { ErrorCode: 400 or 403 or 404 } ||
                    message.EditMessageId is not null && (message.ReplyMarkupJson is null || message.AttemptCount >= 3))
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

    private void MarkSent(TelegramOutboxMessage message)
    {
        message.Status = OutboxMessageStatus.Sent;
        message.SentAt = _timeProvider.GetUtcNow().UtcDateTime;
        message.LastError = null;
    }

    private sealed class OwnerLock(OwnDayDbContext db, long owner) : IAsyncDisposable
    {
        public static async Task<OwnerLock> AcquireAsync(OwnDayDbContext db, long owner, CancellationToken token)
        {
            // Use the same fence as the Learning writers without holding a DB transaction during Telegram I/O.
            await db.Database.OpenConnectionAsync(token);
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({owner})", token);
                return new(db, owner);
            }
            catch
            {
                await db.Database.CloseConnectionAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Resource cleanup needs its own short budget even when delivery was cancelled.
                using var release = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({owner})", release.Token);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
