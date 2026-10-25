using System.Text.Json;
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
    private InboxAnalysisJob() { }

    public static InboxAnalysisJob Schedule(InboxItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new InboxAnalysisJob
        {
            Item = item, InboxItemId = item.Id, UserId = item.UserId,
            CreatedAt = item.CapturedAt, AvailableAt = item.CapturedAt
        };
    }

    public void AttachAnalysis(InboxAnalysis analysis)
    {
        if (Status != InboxAnalysisJobStatus.Running || AnalysisId is not null ||
            analysis.UserId != UserId || analysis.InboxItemId != InboxItemId)
        {
            throw new InvalidOperationException("Analysis must belong to the running job.");
        }

        AnalysisId = analysis.Id;
    }

    // Called under the store's job and Inbox locks, after validating lease ownership.
    public InboxAnalysisJobResult Finish(InboxItem? item, InboxAnalysis? analysis,
        InboxAnalysisOutcome outcome, TimeSpan? retryAfter, DateTime now)
    {
        if (Status != InboxAnalysisJobStatus.Running || LeaseUntil is null || LeaseUntil <= now)
        {
            throw new InvalidOperationException("Only a running job with a valid lease can finish.");
        }

        var code = outcome.Code;
        if (analysis is { Status: not InboxAnalysisStatus.Pending })
        {
            Status = analysis.Status == InboxAnalysisStatus.Completed
                ? InboxAnalysisJobStatus.Completed : InboxAnalysisJobStatus.Failed;
            code = InboxAnalysisCode.AlreadyFinished;
        }
        else if (item?.Status != InboxItemStatus.Active || code == InboxAnalysisCode.ItemInactive)
        {
            Status = InboxAnalysisJobStatus.Skipped;
            code = InboxAnalysisCode.ItemInactive;
            analysis?.Finish(InboxAnalysisStatus.Failed, now, InboxAnalysisOutcome.Failure(code).DiagnosticsJson);
        }
        else if (retryAfter is { } delay)
        {
            Status = InboxAnalysisJobStatus.Ready;
            AvailableAt = now + delay;
        }
        else
        {
            Status = outcome.Output is null ? InboxAnalysisJobStatus.Failed : InboxAnalysisJobStatus.Completed;
            analysis?.Finish(outcome.Status, now, outcome.DiagnosticsJson, outcome.Output, outcome.Model);
        }

        LastCode = code.ToStorageValue();
        LeaseToken = null;
        LeaseUntil = null;
        return new(Status, code);
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public long InboxItemId { get; private set; }
    public UserId UserId { get; private set; }
    public InboxItem Item { get; private set; } = null!;
    public Guid? AnalysisId { get; private set; }
    public InboxAnalysisJobStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime AvailableAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTime? LeaseUntil { get; private set; }
    public string? LastCode { get; private set; }
}

public sealed record InboxAnalysisLease(Guid JobId, Guid Token, UserId UserId, long ItemId, int Attempt);
public sealed record InboxAnalysisJobResult(InboxAnalysisJobStatus Status, InboxAnalysisCode Code);

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

public sealed record InboxAnalysisOutcome(InboxAnalysisCode Code, InboxAnalysisOutput? Output = null,
    string? Model = null, string DiagnosticsJson = "{}")
{
    public InboxAnalysisStatus Status => Output is not null
        ? InboxAnalysisStatus.Completed
        : Code == InboxAnalysisCode.InvalidResponse ? InboxAnalysisStatus.InvalidResponse : InboxAnalysisStatus.Failed;

    public static InboxAnalysisOutcome Failure(InboxAnalysisCode code) => new(code,
        DiagnosticsJson: JsonSerializer.Serialize(new { code = code.ToStorageValue() }, InboxAnalysisJson.Options));
}

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

    public static bool IsTransient(InboxAnalysisCode code) => code is InboxAnalysisCode.ProviderFailure or InboxAnalysisCode.RateLimit or InboxAnalysisCode.Timeout;

    public TimeSpan? RetryAfter(InboxAnalysisCode code, int attempt, double jitter)
    {
        if (!IsTransient(code) || attempt >= MaxAttempts)
        {
            return null;
        }

        var cap = Math.Min(MaxBackoffSeconds, InitialBackoffSeconds * Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(cap * (0.5 + Math.Clamp(jitter, 0, 1) * 0.5));
    }
}
