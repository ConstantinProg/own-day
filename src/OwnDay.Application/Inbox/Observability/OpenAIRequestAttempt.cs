namespace OwnDay.Application.Inbox.Observability;

// One admission per actual provider invocation, independent of logical analysis completion/lease ownership.
public sealed class OpenAIRequestAttempt
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid AnalysisId { get; init; }
    public long UserId { get; init; }
    public int Sequence { get; init; }
    public string Model { get; set; } = string.Empty;
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; set; }
    public string Code { get; set; } = "pending";
    public long ReservedTokens { get; init; }
    public long ChargedTokens { get; set; }
    public int? InputTokens { get; set; }
    public int? CachedInputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public decimal? InputUsdPerMillion { get; set; }
    public decimal? CachedInputUsdPerMillion { get; set; }
    public decimal? OutputUsdPerMillion { get; set; }
    public decimal? EstimatedUsd { get; set; }
    public string? ProviderRequestId { get; set; }
}

public sealed class OpenAICostOptions
{
    public const string SectionName = "InboxAnalysis:CostControl";
    // Global lifetime admission budget in this database. Null explicitly disables admission limits.
    public long? TokenLimit { get; set; } = 1_000_000;
    public long TokensPerRequest { get; set; } = 100_000;
    public Dictionary<string, OpenAIModelPrice> Models { get; set; } = new(StringComparer.Ordinal);
}

public sealed class OpenAIModelPrice
{
    public decimal? InputUsdPerMillion { get; set; }
    public decimal? CachedInputUsdPerMillion { get; set; }
    public decimal? OutputUsdPerMillion { get; set; }

    public decimal? Estimate(int? input, int? cached, int? output)
    {
        if (input is null or < 0 || cached is null or < 0 || output is null or < 0 || cached > input ||
            InputUsdPerMillion is null or < 0 || CachedInputUsdPerMillion is null or < 0 || OutputUsdPerMillion is null or < 0)
        {
            return null;
        }
        return ((input.Value - cached.Value) * InputUsdPerMillion.Value +
            cached.Value * CachedInputUsdPerMillion.Value + output.Value * OutputUsdPerMillion.Value) / 1_000_000m;
    }
}
