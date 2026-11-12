using OwnDay.Domain;

namespace OwnDay.Application.Learning.Requests;

public sealed record LearningRequestLease(Guid RequestId, Guid Token, UserId UserId, int Attempt);
public sealed record LearningRequestExecution(LearningResult? Result, string Outcome)
{
    public IReadOnlyList<string>? Replies { get; init; }
}

public sealed class LearningAppliedOperation
{
    public Guid RequestId { get; init; }
    public string Step { get; init; } = "command";
    public DateTime AppliedAt { get; init; }
}

public interface ILearningRequestStore
{
    Task<LearningRequestLease?> ClaimAsync(TimeSpan lease, CancellationToken token);
    // Invoke a prepared/no-provider operation and persist its effect, marker, reply and completion atomically.
    Task<bool> ApplyAsync(LearningRequestLease lease,
        Func<LearningRequest, CancellationToken, Task<LearningRequestExecution>> execute, CancellationToken token);
}

public interface ILearningRequestRunner
{
    Task<bool> RunAsync(LearningRequestLease lease, int maxAttempts, CancellationToken token);
}

public sealed class LearningRequestProcessor(ILearningRequestRunner runner)
{
    public Task<bool> ProcessAsync(LearningRequestLease lease, int maxAttempts, CancellationToken token) =>
        runner.RunAsync(lease, maxAttempts, token);
}
