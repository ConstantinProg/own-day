using OwnDay.Domain;

namespace OwnDay.Application.Learning.Requests;

// Processing state, separate from pedagogical state and Telegram delivery state.
public enum LearningRequestStatus
{
    Completed,
    Ready,
    Running
}

public sealed record LearningCommandInput(string Name, string Arguments);

public sealed class LearningRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public UserId UserId { get; init; }
    public long Sequence { get; init; }
    public string SourceKey { get; init; } = string.Empty;
    public int SourceUpdateId { get; init; }
    public int? SourceMessageId { get; init; }
    public long ChatId { get; init; }
    public string PayloadKind { get; init; } = "learning_command";
    public int PayloadVersion { get; init; } = 1;
    public string NormalizedInput { get; init; } = string.Empty;
    public LearningRequestStatus Status { get; init; }
    // A reply intent was committed, including validation/provider failure replies.
    // This does not assert successful learning or delivery to Telegram.
    public string? Outcome { get; init; } = "reply_committed";
    public DateTime ReceivedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTime AvailableAt { get; init; }
    public int AttemptCount { get; init; }
    public Guid? LeaseToken { get; init; }
    public DateTime? LeaseUntil { get; init; }
}
