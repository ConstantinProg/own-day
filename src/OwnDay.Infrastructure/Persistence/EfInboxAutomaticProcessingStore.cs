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

    public async Task QueueFeedbackAsync(InboxAutomaticApplication application, InboxAnalysisStatus status,
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
            return;
        }
        var locale = await db.TelegramUserLanguages.Where(l => l.UserId == application.UserId.Value)
            .Select(l => l.Locale).SingleOrDefaultAsync(cancellationToken) ?? destination.Locale;
        db.TelegramOutboxMessages.Add(TelegramInboxFeedback.Render(application, status, destination.ChatId, locale));
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

    public void Add(InboxAutomaticApplication application) => db.InboxAutomaticApplications.Add(application);
}
