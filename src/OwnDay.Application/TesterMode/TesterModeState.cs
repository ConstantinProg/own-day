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

    internal bool IsBlocked(DateTimeOffset now) => FailedAttempts >= MaximumFailures && AttemptWindowEndsAt > now;

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
    }
}
