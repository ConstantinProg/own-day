using OwnDay.Domain;

namespace OwnDay.Domain.WaitingFors;

public sealed class WaitingFor
{
    public const int MaxDescriptionLength = 200;
    public const int MaxSourceLength = 200;

    private WaitingFor() { }

    private WaitingFor(UserId userId, string description, string? source, DateTime waitingSince, long? projectId)
    {
        UserId = userId;
        Description = description;
        Source = source;
        WaitingSince = waitingSince;
        CreatedAt = waitingSince;
        ProjectId = projectId;
        Status = WaitingForStatus.Active;
    }

    public long Id { get; private set; }
    public UserId UserId { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public string? Source { get; private set; }
    public DateTime WaitingSince { get; private set; }
    public WaitingForStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ResolvedAt { get; private set; }
    public long? ProjectId { get; private set; }

    public static WaitingFor Create(UserId userId, string description, string? source, DateTime waitingSince, long? projectId = null)
    {
        if (!userId.IsValid)
        {
            throw new ArgumentException("A valid user is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > MaxDescriptionLength)
        {
            throw new ArgumentException("Description must contain 1 to 200 characters.", nameof(description));
        }

        if (source is not null && (string.IsNullOrWhiteSpace(source) || source.Trim().Length > MaxSourceLength))
        {
            throw new ArgumentException("Source must contain 1 to 200 characters when supplied.", nameof(source));
        }

        if (projectId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(projectId));
        }

        if (waitingSince.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Waiting start must be UTC.", nameof(waitingSince));
        }

        return new WaitingFor(userId, description.Trim(), source?.Trim(), waitingSince, projectId);
    }

    public bool Resolve(DateTime resolvedAt)
    {
        if (Status == WaitingForStatus.Resolved)
        {
            return false;
        }

        Status = WaitingForStatus.Resolved;
        ResolvedAt = resolvedAt;
        return true;
    }
}
