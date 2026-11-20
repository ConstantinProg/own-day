using OwnDay.Infrastructure.Telegram.Commands;

namespace OwnDay.Infrastructure.Learning;

public sealed record LearningRequestExecution(string Outcome, IReadOnlyList<TelegramLearningReply>? Replies = null)
{
    public string? DialogueOutcomeJson { get; init; }
    public bool RemoveSourceKeyboard { get; init; }
}
