namespace OwnDay.Infrastructure.Diagnostics;

public sealed class DiagnosticOptions
{
    public const string SectionName = "Diagnostics";
    public int QueueCapacity { get; init; } = 512;
    public int BatchSize { get; init; } = 32;
    public int MaxExportBytes { get; init; } = 8 * 1024 * 1024;
    public int ExportTimeoutSeconds { get; init; } = 30;
    public int DocumentLifetimeMinutes { get; init; } = 60;
    public const int RetentionDays = 30;
}
