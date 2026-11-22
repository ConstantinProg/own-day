using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using OwnDay.Application.TesterMode;
using OwnDay.Infrastructure.OpenAIAnalysis;
using OwnDay.Infrastructure.Telegram.Configuration;

namespace OwnDay.Infrastructure.Diagnostics;

public sealed partial class DiagnosticText
{
    private readonly string[] _configuredSecrets;

    public DiagnosticText(IOptions<TelegramOptions> telegram, IOptions<OpenAIInboxAnalysisOptions> provider,
        IConfiguration? configuration = null)
    {
        var connectionString = configuration?.GetConnectionString("Default");
        string? databasePassword = null;
        if (connectionString is not null)
        {
            try { databasePassword = new NpgsqlConnectionStringBuilder(connectionString).Password; }
            catch (ArgumentException) { }
        }
        _configuredSecrets = new[] { telegram.Value.BotToken, telegram.Value.WebhookSecret,
            telegram.Value.CallbackSigningKey, provider.Value.ApiKey, connectionString, databasePassword }
            .OfType<string>().Where(secret => !string.IsNullOrWhiteSpace(secret)).OrderByDescending(secret => secret.Length).ToArray();
    }

    internal string? Filter(string? value)
    {
        if (value is null || HasProtectedText(value)) return null;
        if (value.AsSpan().TrimStart() is ['{' or '[', ..])
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                if (HasProtectedFields(document.RootElement)) return null;
            }
            catch (JsonException) { }
        }
        return value;
    }

    private bool HasProtectedText(string value)
    {
        if (TesterModeService.ContainsSecretPrefix(value)) return true;
        foreach (var secret in _configuredSecrets)
            if (value.Contains(secret, StringComparison.Ordinal)) return true;
        return CredentialPattern().IsMatch(value);
    }

    private bool HasProtectedFields(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(property =>
            IsProtectedField(property.Name) || HasProtectedText(property.Name) || HasProtectedFields(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasProtectedFields),
        JsonValueKind.String => HasProtectedText(element.GetString()!),
        _ => false
    };

    private static bool IsProtectedField(string name) => name.Replace("_", "", StringComparison.Ordinal)
        .Replace("-", "", StringComparison.Ordinal).ToLowerInvariant() is
        "password" or "passwd" or "apikey" or "authorization" or "accesstoken" or "bottoken" or "webhooksecret" or
        "secret" or "connectionstring" or "signingkey" or "privatekey" or "callbackdata" or "testeraccesscode" or
        "chainofthought" or "hiddenreasoning" or "reasoningsummary" or "reasoningcontent";

    internal JsonElement? Json(string? value)
    {
        if (Filter(value) is not { } safe) return null;
        using var document = JsonDocument.Parse(safe);
        return document.RootElement.Clone();
    }

    internal object? Keyboard(string? value)
    {
        if (value is null) return null;
        using var document = JsonDocument.Parse(value);
        if (!document.RootElement.TryGetProperty("inline_keyboard", out var keyboard)) return null;
        return keyboard.EnumerateArray().Select(row => row.EnumerateArray().Select(button => new
        {
            text = button.TryGetProperty("text", out var label) ? Filter(label.GetString()) : null,
            kind = button.TryGetProperty("callback_data", out _) ? "callback" :
                button.TryGetProperty("url", out _) ? "url" : "other"
        }).ToArray()).ToArray();
    }

    [GeneratedRegex("""(?i)\b(?:password|passwd|pwd|api[_ -]?key|secret|token|connection[_ -]?string|authorization)\b\s*["']?\s*[:=]|\bBearer\s+[\w.-]+|\bsk-[\w-]{12,}|\b\d{6,}:[\w-]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|\b(?:postgres(?:ql)?|mysql|amqps?)://[^\s"']+""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CredentialPattern();
}
