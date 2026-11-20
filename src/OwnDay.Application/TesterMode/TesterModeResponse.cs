using OwnDay.Domain.Dialogue;

namespace OwnDay.Application.TesterMode;

public enum TesterModeResult
{
    Disabled, AuthorizationRequired, PromptForSecret, InvalidSecret, Locked, Active, Revoked, Cancelled, Expired,
    Status, Confirm, Reset, InvalidCommand
}

public enum TesterModeOperation
{
    Reset = 1
}

public sealed record TesterModeConfirmation(Guid Id, TesterModeOperation Operation, DateTimeOffset ExpiresAt);

public sealed record TesterModeStatus(int ActiveTasks, int CompletedTasks, int Programs, int? CompletedTopics,
    int? TotalTopics, int ActiveExercises, int PausedExercises, int SuspendedExercises, DialoguePhase? Phase,
    DialogueExpectedResponse? ExpectedResponse, int PendingLearningRequests);

public sealed record TesterModeResponse(TesterModeResult Result, TesterModeConfirmation? Confirmation = null, TesterModeStatus? Status = null)
{
    public static implicit operator TesterModeResponse(TesterModeResult result) => new(result);
}
