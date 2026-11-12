using System.Text.Json;
using OwnDay.Domain;

namespace OwnDay.Application.Learning.Requests;

public sealed record LearningRequestLease(Guid RequestId, Guid Token, UserId UserId, int Attempt);
public sealed record LearningRequestExecution(LearningResult? Result, string Outcome);

public sealed class LearningAppliedOperation
{
    public Guid RequestId { get; init; }
    public string Step { get; init; } = "command";
    public DateTime AppliedAt { get; init; }
}

public interface ILearningRequestStore
{
    Task<LearningRequestLease?> ClaimAsync(TimeSpan lease, CancellationToken token);
    // Invoke the no-provider operation and persist its effect, marker, reply and completion atomically.
    Task<bool> ApplyAsync(LearningRequestLease lease,
        Func<LearningRequest, CancellationToken, Task<LearningRequestExecution>> execute, CancellationToken token);
}

public sealed class LearningRequestProcessor(ILearningRequestStore store, LearningService learning)
{
    public Task<bool> ProcessAsync(LearningRequestLease lease, int maxAttempts, CancellationToken token) =>
        store.ApplyAsync(lease, (request, cancellationToken) => ExecuteAsync(request, maxAttempts, cancellationToken), token);

    private async Task<LearningRequestExecution> ExecuteAsync(LearningRequest request, int maxAttempts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.AttemptCount > maxAttempts)
            return new(null, "attempts_exhausted");
        if (request.PayloadKind != "learning_command" || request.PayloadVersion != 1)
            return new(null, "unsupported_payload");
        LearningCommandInput? input;
        try
        {
            input = JsonSerializer.Deserialize<LearningCommandInput>(request.NormalizedInput, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return new(null, "unsupported_payload");
        }
        // Provider-dependent commands deliberately remain unavailable in this lane.
        var result = input?.Name switch
        {
            "learn_pause" => await learning.PauseAsync(request.UserId, token),
            "learn_resume" => await learning.ResumeAsync(request.UserId, token),
            "learn_finish" => await learning.FinishAsync(request.UserId, token),
            _ => null
        };
        return result is null ? new(null, "unsupported_payload") : new(result, "reply_committed");
    }
}
