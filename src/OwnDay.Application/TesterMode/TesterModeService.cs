using System.Security.Cryptography;
using System.Text;
using OwnDay.Domain;

namespace OwnDay.Application.TesterMode;

public enum TesterModeResult
{
    Disabled, AuthorizationRequired, PromptForSecret, InvalidSecret, Locked, Active, Revoked, Cancelled, Expired
}

public sealed class TesterModeService(ITesterModeStore store, TesterModeOptions options)
{
    private const string SecretPrefix = "ownday-tester-";
    private readonly byte[]? _secretHash = options.HasValidSecretHash() ? Convert.FromHexString(options.SecretHash!) : null;

    public bool Enabled => options.Enabled;

    public static bool IsTesterCommand(string? command) =>
        command is "tester" or "tester_off" or "tester_status" or "tester_reset" or "tester_delete_all";

    public static bool ContainsSecretPrefix(string? text) => text?.Contains(SecretPrefix, StringComparison.OrdinalIgnoreCase) == true;

    public async Task<TesterModeResult?> HandleAsync(UserId user, int updateId, string? command, string? text,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var containsSecret = ContainsSecretPrefix(text);
        if (!Enabled)
            return IsTesterCommand(command) || containsSecret ? TesterModeResult.Disabled : null;

        var state = await store.FindAsync(user, cancellationToken);
        if (command == "tester" && !containsSecret)
        {
            if (state is null)
            {
                state = new TesterModeState(user);
                store.Add(state);
            }
            if (state.Authorized)
                return TesterModeResult.Active;
            if (state.IsBlocked(now))
                return TesterModeResult.Locked;
            // A delayed command cannot replace a newer prompt.
            if (updateId >= state.PromptUpdateId)
                state.RequestSecret(updateId, now.AddSeconds(options.ConfirmationTimeoutSeconds));
            return TesterModeResult.PromptForSecret;
        }
        if (command == "tester_off" && !containsSecret)
        {
            state?.Revoke();
            return TesterModeResult.Revoked;
        }
        if (command == "cancel" && state?.PromptExpiresAt > now && !containsSecret)
        {
            state.ClosePrompt();
            return TesterModeResult.Cancelled;
        }
        if (IsTesterCommand(command) && !containsSecret)
            return TesterModeResult.AuthorizationRequired;

        if (!containsSecret && (command is not null || state?.PromptExpiresAt is null))
            return null;
        if (state?.IsBlocked(now) == true)
            return TesterModeResult.Locked;
        if (state?.PromptExpiresAt is not { } expiry || command is not null)
            return TesterModeResult.AuthorizationRequired;
        if (expiry <= now)
        {
            state.ClosePrompt();
            return TesterModeResult.Expired;
        }
        if (updateId <= state.PromptUpdateId)
            return TesterModeResult.AuthorizationRequired;
        if (MatchesSecret(text))
        {
            state.Authorize();
            return TesterModeResult.Active;
        }

        state.RejectSecret(now);
        return state.IsBlocked(now) ? TesterModeResult.Locked : TesterModeResult.InvalidSecret;
    }

    private bool MatchesSecret(string? text)
    {
        var code = text?.Trim();
        return _secretHash is not null && code is not null &&
            code.Length == SecretPrefix.Length + 64 && code.StartsWith(SecretPrefix, StringComparison.Ordinal) &&
            code[SecretPrefix.Length..].All(Uri.IsHexDigit) &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(code)), _secretHash);
    }
}
