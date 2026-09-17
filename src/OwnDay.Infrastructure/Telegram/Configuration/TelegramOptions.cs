namespace OwnDay.Infrastructure.Telegram.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string? BotToken { get; init; }

    public string? BotUsername { get; init; }

    public string? WebhookSecret { get; init; }
}
