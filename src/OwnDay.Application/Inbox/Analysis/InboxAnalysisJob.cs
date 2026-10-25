using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox.Analysis;

public enum InboxAnalysisJobStatus
{
    Ready,
    Running,
    Completed,
    Failed,
    Skipped
}

// One automatic analysis per captured item; retries retain the same AnalysisId/version.
public sealed class InboxAnalysisJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long InboxItemId { get; set; }
    public UserId UserId { get; set; }
    public InboxItem Item { get; set; } = null!;
    public Guid? AnalysisId { get; set; }
    public InboxAnalysisJobStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTime AvailableAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? LastCode { get; set; }
}

public sealed record InboxAnalysisLease(Guid JobId, Guid Token, UserId UserId, long ItemId, int Attempt);
public sealed record InboxAnalysisJobResult(InboxAnalysisJobStatus Status, string Code);

public interface IInboxAnalysisJobStore
{
    Task<InboxAnalysisLease?> ClaimAsync(TimeSpan lease, CancellationToken cancellationToken);
    Task<InboxAnalysis?> StartAsync(InboxAnalysisLease lease, InboxAnalyzerDescriptor descriptor,
        string contextJson, string contextFingerprint, string inputFingerprint, CancellationToken cancellationToken);
    // Fenced, atomic result + job completion, or retry scheduling. Null means ownership was lost.
    Task<InboxAnalysisJobResult?> FinishAsync(InboxAnalysisLease lease, InboxAnalysisOutcome outcome, TimeSpan? retryAfter,
        CancellationToken cancellationToken);
    Task RecoverAsync(bool processingEnabled, CancellationToken cancellationToken);
}

public sealed record InboxAnalysisOutcome(string Code, InboxAnalysisOutput? Output = null,
    string? Model = null, string DiagnosticsJson = "{}");

public sealed class InboxAnalysisProcessingOptions
{
    public const string SectionName = "InboxAnalysis:Background";
    public bool Enabled { get; set; } = true;
    public int Parallelism { get; set; } = 1;
    public int PollSeconds { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 120;
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 3;
    public int InitialBackoffSeconds { get; set; } = 30;
    public int MaxBackoffSeconds { get; set; } = 900;

    public static bool IsTransient(string code) => code is "provider_failure" or "rate_limit" or "timeout";

    public TimeSpan? RetryAfter(string code, int attempt, double jitter)
    {
        if (!IsTransient(code) || attempt >= MaxAttempts)
        {
            return null;
        }

        var cap = Math.Min(MaxBackoffSeconds, InitialBackoffSeconds * Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(cap * (0.5 + Math.Clamp(jitter, 0, 1) * 0.5));
    }
}
