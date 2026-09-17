namespace OwnDay.Infrastructure.Persistence;

public enum OutboxMessageStatus
{
    Pending,
    Sent,
    Failed
}
