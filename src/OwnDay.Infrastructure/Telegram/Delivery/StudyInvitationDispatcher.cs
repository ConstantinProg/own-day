using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Commands;

namespace OwnDay.Infrastructure.Telegram.Delivery;

public sealed class StudyInvitationDispatcher(OwnDayDbContext db, StudyService service, TimeProvider clock)
{
    public async Task<BatchProcessingResult> DispatchBatchAsync(CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var users = await db.Set<StudySchedule>().AsNoTracking().Where(s => !s.Paused && s.NextAt <= now)
            .OrderBy(s => s.NextAt).ThenBy(s => s.UserId).Select(s => s.UserId).Take(50).ToListAsync(token);
        foreach (var user in users)
        {
            // Share the webhook lock so commands, scheduling and invitation delivery serialize per learner.
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({user.Value})", token);
            db.ChangeTracker.Clear();
            var invitation = await service.PlanAsync(user, token);
            if (invitation is not null)
            {
                var at = clock.GetUtcNow().UtcDateTime;
                db.TelegramOutboxMessages.Add(new TelegramOutboxMessage
                {
                    Id = Guid.NewGuid(), ChatId = user.Value, Text = TelegramStudyCommandHandler.Invitation(invitation),
                    StudyInvitationId = invitation.Id, Status = OutboxMessageStatus.Pending, CreatedAt = at, NextAttemptAt = at
                });
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        return new BatchProcessingResult(users.Count == 50);
    }
}
