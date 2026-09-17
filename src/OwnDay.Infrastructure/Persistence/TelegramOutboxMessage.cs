namespace OwnDay.Infrastructure.Persistence;

public sealed class TelegramOutboxMessage
{
    public Guid Id { get; init; }

    public long ChatId { get; init; }

    public string Text { get; init; } = string.Empty;

    public OutboxMessageStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public DateTime NextAttemptAt { get; set; }

    public DateTime CreatedAt { get; init; }

    public DateTime? SentAt { get; set; }

    public string? LastError { get; set; }
}
