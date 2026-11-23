namespace OwnDay.Infrastructure.Persistence;

public sealed class TelegramUserLanguage
{
    public long UserId { get; init; }

    public string? Locale { get; set; }

    public DateTime? SelectionExpiresAt { get; set; }

    public int? SelectionUpdateId { get; set; }
}
