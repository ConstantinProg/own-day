using System.Text.Json;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain;
using OwnDay.Domain.Projects;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfInboxPolicyStore(OwnDayDbContext db) : IInboxPolicyStore
{
    public async Task<InboxPolicySnapshot?> ReadAsync(UserId userId, long itemId, Guid analysisId,
        CancellationToken cancellationToken)
    {
        // Do not inherit a weaker isolation level or observe unsaved changes in a caller's tracked entities.
        if (db.Database.CurrentTransaction is not null || Transaction.Current is not null)
        {
            throw new InvalidOperationException("Inbox policy must be evaluated outside an existing transaction.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(db.Database.IsNpgsql()
            ? System.Data.IsolationLevel.RepeatableRead : System.Data.IsolationLevel.Serializable, cancellationToken);
        var item = await db.InboxItems.AsNoTracking().SingleOrDefaultAsync(
            i => i.Id == itemId && i.UserId == userId, cancellationToken);
        var analysis = await db.InboxAnalyses.AsNoTracking().Include(a => a.Proposals)
            .SingleOrDefaultAsync(a => a.Id == analysisId && a.InboxItemId == itemId && a.UserId == userId, cancellationToken);
        if (item is null || analysis is null)
        {
            return null;
        }

        var latest = await db.InboxAnalyses.Where(a => a.InboxItemId == itemId && a.UserId == userId)
            .MaxAsync(a => a.Version, cancellationToken);
        long[] projectIds;
        try
        {
            var context = JsonSerializer.Deserialize<InboxAnalysisContext>(analysis.ContextJson, InboxAnalysisJson.Options);
            projectIds = context?.Projects?.Where(p => p is not null).Take(InboxAnalysisService.MaxProjects)
                .Select(p => p.Id).ToArray() ?? [];
        }
        catch (JsonException)
        {
            projectIds = []; // The pure engine reports InvalidContext.
        }

        var projects = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id) &&
                p.UserId == userId && p.Status == ProjectStatus.Active)
            .OrderBy(p => p.Id).Select(p => new AnalysisProject(p.Id, p.Title, p.CreatedAt))
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(item, analysis, latest, projects.AsReadOnly());
    }
}
