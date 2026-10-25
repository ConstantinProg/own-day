using OwnDay.Domain;

namespace OwnDay.Application.Inbox.Policy;

public sealed class InboxPolicyService(IInboxPolicyStore store, InboxPolicyEngine engine)
{
    public Task<InboxPolicyDecision> EvaluateAsync(UserId userId, long itemId, Guid analysisId,
        CancellationToken cancellationToken) => EvaluateAsync(userId, itemId, analysisId, InboxPolicyEngine.Version, cancellationToken);

    public async Task<InboxPolicyDecision> EvaluateAsync(UserId userId, long itemId, Guid analysisId,
        string policyVersion, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId.Value);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await store.ReadAsync(userId, itemId, analysisId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return engine.Evaluate(userId, itemId, analysisId, snapshot, policyVersion);
    }
}
