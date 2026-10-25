namespace OwnDay.Infrastructure.Persistence;

// Only Inbox entries captured through Telegram may produce proactive Telegram feedback.
public sealed class TelegramInboxDestination
{
    public long InboxItemId { get; init; }
    public long ChatId { get; init; }
    public string Locale { get; init; } = "en";
}
