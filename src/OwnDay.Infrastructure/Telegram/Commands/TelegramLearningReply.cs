using OwnDay.Application.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed record TelegramLearningReply(string Text, string? ReplyMarkupJson = null, int? EditMessageId = null)
{
    public IReadOnlyList<LearningAction>? Actions { get; init; }
    public ChoicePrompt? Choice { get; init; }
    public bool? InRussian { get; init; }

    internal static TelegramLearningReply WithActions(string text, IReadOnlyList<LearningAction> actions, bool? inRussian = null) =>
        new(text) { Actions = actions, InRussian = inRussian };

    public static implicit operator TelegramLearningReply(string text) => new(text);
}
