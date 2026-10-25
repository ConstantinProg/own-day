using OwnDay.Domain;

namespace OwnDay.Application.Inbox.Analysis;

public interface IInboxAnalysisStore
{
    // Bounded and ordered by ID. Returns at most limit active projects belonging to this user.
    Task<IReadOnlyList<AnalysisProject>> GetProjectsAsync(UserId userId, int limit, CancellationToken cancellationToken);

    // Allocates a version under the existing Inbox lock and commits before returning.
    Task<InboxAnalysis?> StartAsync(UserId userId, long itemId, InboxAnalyzerDescriptor descriptor,
        string contextJson, string contextFingerprint, string inputFingerprint, DateTime createdAt,
        CancellationToken cancellationToken);

    Task SaveAsync(InboxAnalysis analysis, CancellationToken cancellationToken);
    Task<IReadOnlyList<InboxAnalysis>> GetAsync(UserId userId, long itemId, CancellationToken cancellationToken);
}
