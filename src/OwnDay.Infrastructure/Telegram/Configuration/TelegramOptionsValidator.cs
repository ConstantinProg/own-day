using Microsoft.Extensions.Options;

namespace OwnDay.Infrastructure.Telegram.Configuration;

public sealed class TelegramOptionsValidator : IValidateOptions<TelegramOptions>
{
    public ValidateOptionsResult Validate(string? name, TelegramOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BotToken))
        {
            failures.Add("Telegram:BotToken is required.");
        }

        if (string.IsNullOrWhiteSpace(options.BotUsername))
        {
            failures.Add("Telegram:BotUsername is required.");
        }

        if (string.IsNullOrWhiteSpace(options.WebhookSecret))
        {
            failures.Add("Telegram:WebhookSecret is required.");
        }

        Span<byte> signingKey = stackalloc byte[32];
        if (!Convert.TryFromBase64String(options.CallbackSigningKey ?? "", signingKey, out var keyLength) || keyLength != signingKey.Length)
        {
            failures.Add("Telegram:CallbackSigningKey must be a 32-byte key encoded in Base64.");
        }
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(signingKey);

        return failures.Count is 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
