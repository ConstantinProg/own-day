using System.Text.Json;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox;

public sealed record InboxApplicationCandidate(UserId UserId, long InboxItemId, Guid AnalysisId);
public sealed record AppliedInboxProposal(Guid ProposalId, InboxTargetKind Kind, long TargetId, long? ProjectId);

internal sealed record PreparedInboxProposal(Guid ProposalId, ProcessInboxItem Command);

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
            var evaluation = policy.PrepareApplication(userId, itemId, analysisId, snapshot);
            var decision = evaluation.Decision;
            IReadOnlyList<AppliedInboxProposal> applied = [];
            if (decision.Route == InboxPolicyRoute.AutoApply)
            {
                applied = await processing.ProcessBatchLockedAsync(userId, item, evaluation.Proposals, token);
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
}
