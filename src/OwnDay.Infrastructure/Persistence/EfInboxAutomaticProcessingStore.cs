using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.Telegram.Handling;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfInboxAutomaticProcessingStore(OwnDayDbContext db) : IInboxAutomaticProcessingStore
{
    public Task<InboxApplicationCandidate?> FindNextAsync(CancellationToken cancellationToken) =>
        db.InboxAnalyses.AsNoTracking().Where(a => a.Status != InboxAnalysisStatus.Pending &&
                !db.InboxAutomaticApplications.Any(r => r.AnalysisId == a.Id))
            .OrderBy(a => a.CompletedAt).ThenBy(a => a.Id)
            .Select(a => new InboxApplicationCandidate(a.UserId, a.InboxItemId, a.Id))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<InboxAutomaticApplication?> FindAsync(UserId userId, Guid analysisId, CancellationToken cancellationToken)
    {
        var application = await db.InboxAutomaticApplications.SingleOrDefaultAsync(
            a => a.AnalysisId == analysisId && a.UserId == userId, cancellationToken);
        if (application is not null)
        {
            // Confirmation may have waited for another transaction after its initial discovery read.
            await db.Entry(application).ReloadAsync(cancellationToken);
        }
        return application;
    }

    public async Task<bool> QueueFeedbackAsync(InboxAutomaticApplication application, InboxAnalysisStatus status,
        CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Feedback requires the Inbox transaction.");
        }
        var destination = await db.TelegramInboxDestinations.AsNoTracking()
            .SingleOrDefaultAsync(d => d.InboxItemId == application.InboxItemId, cancellationToken);
        if (destination is null)
        {
            return false;
        }
        var locale = await db.TelegramUserLanguages.Where(l => l.UserId == application.UserId.Value)
            .Select(l => l.Locale).SingleOrDefaultAsync(cancellationToken) ?? destination.Locale;
        db.TelegramOutboxMessages.Add(TelegramInboxFeedback.Render(application, status, destination.ChatId, locale));
        return true;
    }

    public async Task<InboxPolicySnapshot?> ReadLockedAsync(InboxItem item, Guid analysisId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Automatic application requires the Inbox transaction.");
        }
        var analysis = await db.InboxAnalyses.AsNoTracking().Include(a => a.Proposals).SingleOrDefaultAsync(
            a => a.Id == analysisId && a.InboxItemId == item.Id && a.UserId == item.UserId, cancellationToken);
        if (analysis is null)
        {
            return null;
        }
        var latest = await db.InboxAnalyses.Where(a => a.InboxItemId == item.Id)
            .MaxAsync(a => a.Version, cancellationToken);
        long[] ids;
        try
        {
            ids = JsonSerializer.Deserialize<InboxAnalysisContext>(analysis.ContextJson, InboxAnalysisJson.Options)?
                .Projects?.Where(p => p is not null).Take(InboxAnalysisService.MaxProjects).Select(p => p.Id).ToArray() ?? [];
        }
        catch (JsonException)
        {
            ids = []; // The policy reports invalid context; no domain mutation follows.
        }
        var query = db.Database.IsNpgsql()
            ? db.Projects.FromSqlInterpolated($"""
                SELECT * FROM projects WHERE id = ANY({ids}) AND user_id = {item.UserId.Value}
                    AND status = {(int)ProjectStatus.Active} ORDER BY id FOR SHARE
                """)
            : db.Projects.Where(p => ids.Contains(p.Id) && p.UserId == item.UserId && p.Status == ProjectStatus.Active);
        var projects = await query.AsNoTracking().ToListAsync(cancellationToken);
        return new(item, analysis, latest, projects.Select(p => new AnalysisProject(p.Id, p.Title, p.CreatedAt)).ToArray());
    }

    public async Task<InboxAutomaticApplication?> FindAppliedAsync(UserId userId, long itemId, CancellationToken cancellationToken)
    {
        var application = await db.InboxAutomaticApplications.Where(a => a.UserId == userId && a.InboxItemId == itemId &&
                (a.Route == InboxPolicyRoute.AutoApply || a.ConfirmationOutcome == InboxConfirmationOutcome.Applied))
            .OrderByDescending(a => a.EvaluatedAt).FirstOrDefaultAsync(cancellationToken);
        if (application is not null)
        {
            await db.Entry(application).ReloadAsync(cancellationToken);
        }
        return application;
    }

    private static AppliedInboxProposal[] Targets(InboxAutomaticApplication application) =>
        JsonSerializer.Deserialize<AppliedInboxProposal[]>(application.ConfirmedTargetsJson != "[]"
            ? application.ConfirmedTargetsJson : application.TargetsJson, InboxAnalysisJson.Options)!;

    public async Task CaptureUndoStateAsync(InboxAutomaticApplication application, CancellationToken cancellationToken)
    {
        var states = new List<UndoTargetState>();
        foreach (var target in Targets(application))
        {
            var entity = await ReadTargetLockedAsync(target, application.UserId, cancellationToken)
                ?? throw new InvalidOperationException("Applied target is missing.");
            states.Add(new(target.Kind, target.TargetId, StateHash(entity)));
        }
        application.UndoStateJson = JsonSerializer.Serialize(states, InboxAnalysisJson.Options);
    }

    public async Task<bool> TryRemoveUnchangedTargetsAsync(InboxAutomaticApplication application, CancellationToken cancellationToken)
    {
        var targets = Targets(application);
        var states = JsonSerializer.Deserialize<UndoTargetState[]>(application.UndoStateJson, InboxAnalysisJson.Options)!;
        // Historical applications have no trustworthy creation snapshot. Never infer it from today's data.
        if (states.Length != targets.Length || states.Length == 0 ||
            states.Select(s => (s.Kind, s.Id)).Distinct().Count() != states.Length)
        {
            return false;
        }
        var entities = new List<object>();
        // Lock projects before children, also fencing concurrent FK references until commit.
        foreach (var target in targets.OrderBy(t => t.Kind == InboxTargetKind.Project ? 0 : 1)
                     .ThenBy(t => t.Kind).ThenBy(t => t.TargetId))
        {
            var entity = await ReadTargetLockedAsync(target, application.UserId, cancellationToken);
            if (entity is null || states.SingleOrDefault(s => s.Kind == target.Kind && s.Id == target.TargetId)?.Hash != StateHash(entity))
            {
                return false;
            }
            if (await db.InboxItems.AnyAsync(i => i.Id != application.InboxItemId &&
                    i.Targets.Any(t => t.Kind == target.Kind && t.TargetId == target.TargetId), cancellationToken))
            {
                return false;
            }
            if (target.Kind == InboxTargetKind.Project &&
                (await db.Actions.AnyAsync(a => a.ProjectId == target.TargetId, cancellationToken) ||
                 await db.SomedayMaybes.AnyAsync(a => a.ProjectId == target.TargetId, cancellationToken) ||
                 await db.References.AnyAsync(a => a.ProjectId == target.TargetId, cancellationToken) ||
                 await db.WaitingFors.AnyAsync(a => a.ProjectId == target.TargetId, cancellationToken)))
            {
                return false;
            }
            entities.Add(entity);
        }
        foreach (var entity in entities)
        {
            db.Remove(entity);
        }
        return true;
    }

    // Scalar persisted state only, including lifecycle, text, ownership and project/source/date fields.
    // PostgreSQL stores timestamps at microsecond precision; use the same precision before hashing.
    private string StateHash(object entity)
    {
        var state = db.Entry(entity).Properties.OrderBy(p => p.Metadata.Name, StringComparer.Ordinal).ToDictionary(p => p.Metadata.Name,
            p => p.CurrentValue is DateTime time ? (object)(time.Ticks / 10) : p.CurrentValue);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state))));
    }

    private async Task<object?> ReadTargetLockedAsync(AppliedInboxProposal target, UserId owner, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Undo requires the Inbox transaction.");
        }
        return target.Kind switch
        {
            InboxTargetKind.Action => await ReadAsync(db.Actions, "actions", target.TargetId, owner, token),
            InboxTargetKind.Project => await ReadAsync(db.Projects, "projects", target.TargetId, owner, token),
            InboxTargetKind.SomedayMaybe => await ReadAsync(db.SomedayMaybes, "someday_maybes", target.TargetId, owner, token),
            InboxTargetKind.Reference => await ReadAsync(db.References, "references", target.TargetId, owner, token),
            InboxTargetKind.WaitingFor => await ReadAsync(db.WaitingFors, "waiting_fors", target.TargetId, owner, token),
            _ => null
        };
    }

    private async Task<object?> ReadAsync<T>(DbSet<T> set, string table, long id, UserId owner, CancellationToken token) where T : class
    {
        FormattableString sql = table switch
        {
            "actions" => $"SELECT * FROM actions WHERE id = {id} AND user_id = {owner.Value} FOR UPDATE",
            "projects" => $"SELECT * FROM projects WHERE id = {id} AND user_id = {owner.Value} FOR UPDATE",
            "someday_maybes" => $"SELECT * FROM someday_maybes WHERE id = {id} AND user_id = {owner.Value} FOR UPDATE",
            "references" => $"SELECT * FROM \"references\" WHERE id = {id} AND user_id = {owner.Value} FOR UPDATE",
            "waiting_fors" => $"SELECT * FROM waiting_fors WHERE id = {id} AND user_id = {owner.Value} FOR UPDATE",
            _ => throw new InvalidOperationException("Unsupported Inbox target table.")
        };
        var query = db.Database.IsNpgsql()
            ? set.FromSqlInterpolated(sql)
            : set.Where(e => EF.Property<long>(e, "Id") == id && EF.Property<UserId>(e, "UserId") == owner);
        var entity = (await query.ToListAsync(token)).SingleOrDefault();
        if (entity is not null)
        {
            await db.Entry(entity).ReloadAsync(token);
        }
        return entity;
    }

    private sealed record UndoTargetState(InboxTargetKind Kind, long Id, string Hash);

    public void Add(InboxAutomaticApplication application) => db.InboxAutomaticApplications.Add(application);
}
