using OwnDay.Domain;

namespace OwnDay.Domain.References;

public sealed class Reference
{
    public const int MaxTextLength = 200;

    private Reference() { }

    private Reference(UserId userId, string text, DateTime createdAt, long? projectId)
    {
        UserId = userId;
        Text = text;
        CreatedAt = createdAt;
        ProjectId = projectId;
        Status = ReferenceStatus.Active;
    }

    public long Id { get; private set; }
    public UserId UserId { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public ReferenceStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ArchivedAt { get; private set; }
    public long? ProjectId { get; private set; }

    public static Reference Create(UserId userId, string text, DateTime createdAt, long? projectId = null)
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

        return new Reference(userId, text.Trim(), createdAt, projectId);
    }

    public bool Archive(DateTime archivedAt)
    {
        if (Status == ReferenceStatus.Archived)
        {
            return false;
        }

        Status = ReferenceStatus.Archived;
        ArchivedAt = archivedAt;
        return true;
    }
}
