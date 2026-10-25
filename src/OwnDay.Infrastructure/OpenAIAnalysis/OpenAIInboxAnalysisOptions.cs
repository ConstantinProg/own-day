namespace OwnDay.Infrastructure.OpenAIAnalysis;

public sealed class OpenAIInboxAnalysisOptions
{
    public const string SectionName = "InboxAnalysis:OpenAI";

    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gpt-4.1-mini";
    public int MaxOutputTokens { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 60;
}
