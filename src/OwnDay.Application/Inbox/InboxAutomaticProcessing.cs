using System.Text.Json;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox;

public sealed record InboxApplicationCandidate(UserId UserId, long InboxItemId, Guid AnalysisId);
public sealed record AppliedInboxProposal(Guid ProposalId, InboxTargetKind Kind, long TargetId, long? ProjectId);

internal sealed record PreparedInboxProposal(Guid ProposalId, ProcessInboxItem Command);

public enum InboxConfirmationOutcome
{
    Applied,
    Rejected,
    Stale
}

public sealed record InboxConfirmationResult(InboxConfirmationOutcome? Outcome, long? InboxItemId, bool Changed,
    bool FeedbackQueued = false);

// Original assessment is retained; confirmation resolution is written once under the Inbox lock.
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
    public InboxConfirmationOutcome? ConfirmationOutcome { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string ConfirmedTargetsJson { get; set; } = "[]";
}

public interface IInboxAutomaticProcessingStore
{
    Task<InboxApplicationCandidate?> FindNextAsync(CancellationToken cancellationToken);
    Task<InboxAutomaticApplication?> FindAsync(UserId userId, Guid analysisId, CancellationToken cancellationToken);
    // Called inside the existing Inbox transaction/lock; referenced projects stay locked until commit.
    Task<InboxPolicySnapshot?> ReadLockedAsync(InboxItem item, Guid analysisId, CancellationToken cancellationToken);
    void Add(InboxAutomaticApplication application);
    // Enlist one logical notification in the caller's transaction; return false if no destination exists.
    Task<bool> QueueFeedbackAsync(InboxAutomaticApplication application, InboxAnalysisStatus status, CancellationToken cancellationToken);
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
        CancellationToken cancellationToken) =>
        ApplyAsync(userId, itemId, analysisId, InboxPolicyEngine.Version, cancellationToken);

    public Task<InboxAutomaticApplication?> ApplyAsync(UserId userId, long itemId, Guid analysisId,
        string policyVersion, CancellationToken cancellationToken)
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
            var evaluation = policy.PrepareApplication(userId, itemId, analysisId, snapshot, policyVersion);
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
            // Inactive/superseded histories must not produce misleading attention notifications.
            if (!decision.Reasons.Contains(InboxPolicyReason.InboxInactive) &&
                snapshot.Analysis.Version == snapshot.LatestAnalysisVersion)
            {
                await store.QueueFeedbackAsync(result, snapshot.Analysis.Status, token);
            }
            return result;
        }, cancellationToken);
    }

    public async Task<InboxConfirmationResult> ConfirmAsync(UserId userId, Guid analysisId, bool accept,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId.Value);
        var found = await store.FindAsync(userId, analysisId, cancellationToken);
        if (found is null)
        {
            return new(null, null, false);
        }
        return await locks.ExecuteLockedAsync(userId, found.InboxItemId, async (item, token) =>
        {
            var application = await store.FindAsync(userId, analysisId, token);
            if (item is null || application is null || application.Route != InboxPolicyRoute.RequireConfirmation)
            {
                return new InboxConfirmationResult(null, null, false);
            }
            if (application.ConfirmationOutcome is { } previous)
            {
                return new InboxConfirmationResult(previous, item.Id, false);
            }

            var snapshot = await store.ReadLockedAsync(item, analysisId, token);
            var prepared = snapshot is null ? null : policy.PrepareApplication(userId, item.Id, analysisId,
                snapshot, application.PolicyVersion);
            var original = JsonSerializer.Deserialize<InboxPolicyDecision>(application.DecisionJson, InboxAnalysisJson.Options);
            var current = prepared?.Decision;
            var stale = clock.GetUtcNow().UtcDateTime - application.EvaluatedAt > TimeSpan.FromHours(24) ||
                current?.Route != InboxPolicyRoute.RequireConfirmation || original is null ||
                current.StateFingerprint != original.StateFingerprint;
            var outcome = stale ? InboxConfirmationOutcome.Stale : accept
                ? InboxConfirmationOutcome.Applied : InboxConfirmationOutcome.Rejected;
            if (outcome == InboxConfirmationOutcome.Applied)
            {
                var targets = await processing.ProcessBatchLockedAsync(userId, item, prepared!.Proposals, token);
                application.ConfirmedTargetsJson = JsonSerializer.Serialize(targets, InboxAnalysisJson.Options);
            }
            application.ConfirmationOutcome = outcome;
            application.ResolvedAt = clock.GetUtcNow().UtcDateTime;
            var feedbackQueued = await store.QueueFeedbackAsync(application, snapshot?.Analysis.Status ?? InboxAnalysisStatus.Failed, token);
            return new InboxConfirmationResult(outcome, item.Id, true, feedbackQueued);
        }, cancellationToken);
    }
}
