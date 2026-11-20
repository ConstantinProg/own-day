namespace OwnDay.Application.TesterMode;

public sealed class TesterModeOptions
{
    public const string SectionName = "TesterMode";

    public bool Enabled { get; set; }
    public string? SecretHash { get; set; }
    public int ConfirmationTimeoutSeconds { get; set; } = 120;

    public bool HasValidSecretHash() => SecretHash is { Length: 64 } hash && hash.All(Uri.IsHexDigit);
}
