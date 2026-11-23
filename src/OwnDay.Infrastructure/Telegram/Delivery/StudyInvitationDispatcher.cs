using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Diagnostics;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Actions;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Handling;

namespace OwnDay.Infrastructure.Telegram.Delivery;

public sealed class StudyInvitationDispatcher(OwnDayDbContext db, StudyService service, TimeProvider clock, TelegramActionKeyboard keyboard,
    TelegramLanguageFlow language, DiagnosticJournal? journal = null, DiagnosticText? diagnosticText = null)
{
    public async Task<BatchProcessingResult> DispatchBatchAsync(CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var users = await db.Set<StudySchedule>().AsNoTracking().Where(s => !s.Paused && s.NextAt <= now)
            .OrderBy(s => s.NextAt).ThenBy(s => s.UserId).Select(s => s.UserId).Take(50).ToListAsync(token);
        foreach (var user in users)
        {
            var captured = journal is null ? null : await journal.CaptureAsync(user, Guid.Empty, null, null, token);
            // Share the webhook lock so commands, scheduling and invitation delivery serialize per learner.
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({user.Value})", token);
            db.ChangeTracker.Clear();
            var invitation = await service.PlanAsync(user, token);
            TelegramOutboxMessage? intent = null;
            using var diagnostics = DiagnosticJournal.Enter(invitation is not null && captured is not null
                ? captured with { Correlation = invitation.Id } : null);
            if (invitation is not null)
            {
                var at = clock.GetUtcNow().UtcDateTime;
                var locale = await language.ResolveAsync(user, null, token);
                var reply = keyboard.Render(TelegramStudyCommandHandler.Invitation(invitation, locale.Locale == "ru"), user.Value, user.Value, new(at), null);
                intent = new TelegramOutboxMessage
                {
                    Id = Guid.NewGuid(), ChatId = user.Value, Text = reply.Text, ReplyMarkupJson = reply.ReplyMarkupJson,
                    DiagnosticCorrelationId = invitation.Id,
                    StudyInvitationId = invitation.Id, Status = OutboxMessageStatus.Pending, CreatedAt = at, NextAttemptAt = at
                };
                db.TelegramOutboxMessages.Add(intent);
                journal?.Write("outbox", "reply_formed", entity: intent.Id, detail: () => new
                {
                    text = diagnosticText?.Filter(intent.Text),
                    keyboard = diagnosticText?.Keyboard(intent.ReplyMarkupJson)
                });
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            if (intent is not null) journal?.Write("outbox", "outbox_committed", entity: intent.Id);
        }
        return new BatchProcessingResult(users.Count == 50);
    }
}
