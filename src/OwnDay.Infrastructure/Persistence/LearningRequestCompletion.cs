namespace OwnDay.Infrastructure.Persistence;

public sealed record LearningRequestCompletion(string Outcome, IReadOnlyList<TelegramOutboxIntent>? Replies = null)
{
    public string? DialogueOutcomeJson { get; init; }
}

public sealed record TelegramOutboxIntent(string Text, string? ReplyMarkupJson = null, int? EditMessageId = null)
{
    public static implicit operator TelegramOutboxIntent(string text) => new(text);
}
