using System.Text.Json;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox;

public sealed record InboxApplicationCandidate(UserId UserId, long InboxItemId, Guid AnalysisId);
public sealed record AppliedInboxProposal(Guid ProposalId, InboxTargetKind Kind, long TargetId, long? ProjectId);

// Immutable application history, committed in the same transaction as the domain targets.
public sealed class InboxAutomaticApplication
{
    public Guid AnalysisId { get; init; }
    public long InboxItemId { get; init; }
    public UserId UserId { get; init; }
    public string PolicyVersion { get; init; } = string.Empty;
    public DateTime EvaluatedAt { get; init; }
    public InboxPolicyRoute Route { get; init; }
    public string DecisionJson { get; init; } = "{}";
    public string ProposalsJson { get; init; } = "[]";
    public string TargetsJson { get; init; } = "[]";
}

public interface IInboxAutomaticProcessingStore
{
    Task<InboxApplicationCandidate?> FindNextAsync(CancellationToken cancellationToken);
    Task<InboxAutomaticApplication?> FindAsync(UserId userId, Guid analysisId, CancellationToken cancellationToken);
    // Called inside the existing Inbox transaction/lock; referenced projects stay locked until commit.
    Task<InboxPolicySnapshot?> ReadLockedAsync(InboxItem item, Guid analysisId, CancellationToken cancellationToken);
    void Add(InboxAutomaticApplication application);
}

public sealed class InboxAutomaticProcessingService(
    IInboxProcessingStore locks,
    IInboxAutomaticProcessingStore store,
    InboxPolicyEngine policy,
    InboxProcessingService processing,
    TimeProvider clock)
{
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var candidate = await store.FindNextAsync(cancellationToken);
        if (candidate is null)
        {
            return false;
        }
        await ApplyAsync(candidate.UserId, candidate.InboxItemId, candidate.AnalysisId, cancellationToken);
        return true;
    }

    public Task<InboxAutomaticApplication?> ApplyAsync(UserId userId, long itemId, Guid analysisId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId.Value);
        return locks.ExecuteLockedAsync<InboxAutomaticApplication?>(userId, itemId, async (item, token) =>
        {
            if (item is null)
            {
                return null;
            }
            var previous = await store.FindAsync(userId, analysisId, token);
            if (previous is not null)
            {
                return previous.InboxItemId == itemId ? previous : null;
            }
            var snapshot = await store.ReadLockedAsync(item, analysisId, token);
            if (snapshot is null || snapshot.Analysis.Status == InboxAnalysisStatus.Pending)
            {
                return null;
            }
            var decision = policy.Evaluate(userId, itemId, analysisId, snapshot, InboxPolicyEngine.Version);
            var applied = new List<AppliedInboxProposal>();
            if (decision.Route == InboxPolicyRoute.AutoApply)
            {
                var rows = snapshot.Analysis.Proposals.OrderBy(p => p.Position).ToArray();
                var commands = rows.Select(row => ToCommand(itemId, row.ReadProposal(), snapshot)).ToArray();
                var targets = await processing.ProcessBatchLockedAsync(userId, item, commands, token);
                for (var i = 0; i < targets.Count; i++)
                {
                    applied.Add(new(rows[i].Id, targets[i].Kind, targets[i].TargetId, commands[i].ProjectId));
                }
            }
            var result = new InboxAutomaticApplication
            {
                AnalysisId = analysisId, InboxItemId = itemId, UserId = userId,
                PolicyVersion = decision.PolicyVersion, EvaluatedAt = clock.GetUtcNow().UtcDateTime,
                Route = decision.Route, DecisionJson = JsonSerializer.Serialize(decision, InboxAnalysisJson.Options),
                ProposalsJson = JsonSerializer.Serialize(snapshot.Analysis.Proposals.OrderBy(p => p.Position), InboxAnalysisJson.Options),
                TargetsJson = JsonSerializer.Serialize(applied, InboxAnalysisJson.Options)
            };
            store.Add(result);
            return result;
        }, cancellationToken);
    }

    private static ProcessInboxItem ToCommand(long itemId, InboxProposal proposal, InboxPolicySnapshot snapshot)
    {
        // Project candidates are alternatives. Drop an optional link if it is ambiguous, stale or unavailable.
        var context = JsonSerializer.Deserialize<InboxAnalysisContext>(snapshot.Analysis.ContextJson, InboxAnalysisJson.Options)!;
        long? projectId = proposal.ProjectIds.Length == 1 && snapshot.ActiveOwnedProjects.Any(p =>
            p.Id == proposal.ProjectIds[0] && context.Projects.Contains(p)) ? proposal.ProjectIds[0] : null;
        // All current targets have one 200-character primary text field. Keep the canonical title;
        // additional description and unresolved dates remain intact in analysis and audit.
        return new(itemId, proposal.Kind, proposal.Title, projectId,
            proposal.Properties.Source, proposal.Properties.WaitingSince?.UtcDateTime);
    }
}
