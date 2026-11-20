using OwnDay.Domain;

namespace OwnDay.Application.TesterMode;

public sealed class TesterModeState(UserId userId)
{
    private const int MaximumFailures = 5;
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(15);

    public UserId UserId { get; } = userId;
    public bool Authorized { get; private set; }
    public DateTimeOffset? PromptExpiresAt { get; private set; }
    public int PromptUpdateId { get; private set; }
    public int FailedAttempts { get; private set; }
    public DateTimeOffset? AttemptWindowEndsAt { get; private set; }
    public Guid? ConfirmationId { get; private set; }
    public TesterModeOperation? PendingOperation { get; private set; }

    internal bool IsBlocked(DateTimeOffset now) => FailedAttempts >= MaximumFailures && AttemptWindowEndsAt > now;
    internal bool HasSecretPrompt => PromptExpiresAt is not null && PendingOperation is null;

    internal void RequestSecret(int updateId, DateTimeOffset expiresAt)
    {
        PromptUpdateId = updateId;
        PromptExpiresAt = expiresAt;
    }

    internal void RejectSecret(DateTimeOffset now)
    {
        if (AttemptWindowEndsAt is null || AttemptWindowEndsAt <= now)
        {
            AttemptWindowEndsAt = now + AttemptWindow;
            FailedAttempts = 0;
        }

        FailedAttempts++;
        if (IsBlocked(now))
            ClosePrompt();
    }

    internal void Authorize()
    {
        Authorized = true;
        FailedAttempts = 0;
        AttemptWindowEndsAt = null;
        ClosePrompt();
    }

    internal void Revoke()
    {
        Authorized = false;
        ClosePrompt();
    }

    internal void ClosePrompt()
    {
        PromptUpdateId = 0;
        PromptExpiresAt = null;
        ConfirmationId = null;
        PendingOperation = null;
    }

    internal TesterModeConfirmation RequestConfirmation(TesterModeOperation operation, int updateId, DateTimeOffset expiresAt)
    {
        PromptUpdateId = updateId;
        PromptExpiresAt = expiresAt;
        PendingOperation = operation;
        ConfirmationId = Guid.NewGuid();
        return new(ConfirmationId.Value, operation, expiresAt);
    }

    internal bool MatchesConfirmation(TesterModeConfirmation confirmation, int updateId, DateTimeOffset now) =>
        confirmation.Id == ConfirmationId && confirmation.Operation == PendingOperation &&
        updateId > PromptUpdateId && PromptExpiresAt > now && confirmation.ExpiresAt > now;
}
