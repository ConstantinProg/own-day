namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed record TelegramLearningReply(string Text, string? ReplyMarkupJson = null)
{
    public static implicit operator TelegramLearningReply(string text) => new(text);
}
