namespace OwnDay.Application.Learning.Requests;

// Technical invocation identity, distinct from the logical step/effect and from
// cost reconciliation in the shared request ledger.
public sealed class LearningProviderAttempt
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RequestId { get; init; }
    public int Ordinal { get; init; }
    public int Number { get; init; }
    public Guid LeaseToken { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; set; }
    public string? Outcome { get; set; }
}
