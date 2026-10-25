namespace OwnDay.Infrastructure.OpenAIAnalysis;

public sealed class OpenAIInboxAnalysisOptions
{
    public const string SectionName = "InboxAnalysis:OpenAI";

    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gpt-6-luna";
    public string ReasoningEffort { get; set; } = "low";
    // Includes reasoning and visible JSON output. Never increase automatically after truncation.
    public int MaxOutputTokens { get; set; } = 8192;
    public int TimeoutSeconds { get; set; } = 60;

    // Only send reasoning for the model whose capabilities are verified here. Legacy/custom models
    // still use strict Structured Outputs, but receive no potentially unsupported reasoning parameter.
    public string? EffectiveReasoningEffort => Model == "gpt-6-luna" ? ReasoningEffort : null;
}
