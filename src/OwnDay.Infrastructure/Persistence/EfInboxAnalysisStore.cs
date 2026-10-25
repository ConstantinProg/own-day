using System.Transactions;
using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Domain.Projects;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfInboxAnalysisStore(OwnDayDbContext db, IInboxProcessingStore inboxLocks) : IInboxAnalysisStore
{
    public async Task<IReadOnlyList<AnalysisProject>> GetProjectsAsync(
        UserId userId, int limit, CancellationToken cancellationToken) =>
        await db.Projects.AsNoTracking()
            .Where(project => project.UserId == userId && project.Status == ProjectStatus.Active)
            .OrderBy(project => project.Id).Take(limit)
            .Select(project => new AnalysisProject(project.Id, project.Title, project.CreatedAt))
            .ToListAsync(cancellationToken);

    public Task<InboxAnalysis?> StartAsync(UserId userId, long itemId, InboxAnalyzerDescriptor descriptor,
        string contextJson, string contextFingerprint, string inputFingerprint, DateTime createdAt,
        CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null || Transaction.Current is not null)
        {
            throw new InvalidOperationException("Inbox analysis must be invoked outside an existing transaction.");
        }

        return inboxLocks.ExecuteLockedAsync<InboxAnalysis?>(userId, itemId, async (item, token) =>
        {
            if (item?.Status != InboxItemStatus.Active)
            {
                return null;
            }

            var version = (await db.InboxAnalyses.Where(row => row.InboxItemId == itemId)
                .MaxAsync(row => (int?)row.Version, token) ?? 0) + 1;
            var analysis = InboxAnalysis.Start(userId, itemId, version, descriptor, contextJson,
                contextFingerprint, inputFingerprint, createdAt);
            db.InboxAnalyses.Add(analysis);
            return analysis;
        }, cancellationToken);
    }

    public async Task SaveAsync(InboxAnalysis analysis, CancellationToken cancellationToken) =>
        await db.SaveChangesAsync(cancellationToken);

    public async Task<IReadOnlyList<InboxAnalysis>> GetAsync(UserId userId, long itemId, CancellationToken cancellationToken) =>
        await db.InboxAnalyses.AsNoTracking().Where(row => row.UserId == userId && row.InboxItemId == itemId)
            .Include(row => row.Proposals.OrderBy(proposal => proposal.Position))
            .OrderByDescending(row => row.Version).ToListAsync(cancellationToken);
}
