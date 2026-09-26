using OwnDay.Domain;

namespace OwnDay.Domain.SomedayMaybes;

public sealed class SomedayMaybe
{
    public const int MaxTextLength = 200;

    private SomedayMaybe() { }

    private SomedayMaybe(UserId userId, string text, DateTime createdAt, long? projectId)
    {
        UserId = userId;
        Text = text;
        CreatedAt = createdAt;
        ProjectId = projectId;
        Status = SomedayMaybeStatus.Active;
    }

    public long Id { get; private set; }
    public UserId UserId { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public SomedayMaybeStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ArchivedAt { get; private set; }
    public long? ProjectId { get; private set; }

    public static SomedayMaybe Create(UserId userId, string text, DateTime createdAt, long? projectId = null)
    {
        if (!userId.IsValid)
        {
            throw new ArgumentException("A valid user is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > MaxTextLength)
        {
            throw new ArgumentException("Text must contain 1 to 200 characters.", nameof(text));
        }

        if (projectId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(projectId));
        }

        return new SomedayMaybe(userId, text.Trim(), createdAt, projectId);
    }

    public bool Archive(DateTime archivedAt)
    {
        if (Status == SomedayMaybeStatus.Archived)
        {
            return false;
        }

        Status = SomedayMaybeStatus.Archived;
        ArchivedAt = archivedAt;
        return true;
    }
}
