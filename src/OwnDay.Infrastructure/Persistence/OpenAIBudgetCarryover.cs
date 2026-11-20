namespace OwnDay.Infrastructure.Persistence;

internal sealed class OpenAIBudgetCarryover
{
    public long UserId { get; init; }
    public DateOnly Month { get; init; }
    public long CallCount { get; set; }
    public long ChargedTokens { get; set; }
    public decimal ChargedUsd { get; set; }
    public bool HasUnknownCost { get; set; }
}
