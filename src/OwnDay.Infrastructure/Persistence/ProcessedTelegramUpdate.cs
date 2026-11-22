namespace OwnDay.Infrastructure.Persistence;

public sealed class ProcessedTelegramUpdate
{
    public long UpdateId { get; init; }

    public DateTime ReceivedAt { get; init; }

    public DateTime? ProcessedAt { get; set; }
    public long? DiagnosticOwnerId { get; set; }
    public Guid? DiagnosticCorrelationId { get; set; }
}
