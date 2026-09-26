using OwnDay.Domain;

namespace OwnDay.Domain.Projects;

public sealed class Project
{
    public const int MaxTitleLength = 200;

    private Project() { }

    private Project(UserId userId, string title, DateTime createdAt)
    {
        UserId = userId;
        Title = title;
        CreatedAt = createdAt;
        Status = ProjectStatus.Active;
    }

    public long Id { get; private set; }
    public UserId UserId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public ProjectStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    public static Project Create(UserId userId, string title, DateTime createdAt)
    {
        if (!userId.IsValid)
        {
            throw new ArgumentException("A valid user is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitleLength)
        {
            throw new ArgumentException("Title must contain 1 to 200 characters.", nameof(title));
        }

        return new Project(userId, title.Trim(), createdAt);
    }

    public bool Complete(DateTime completedAt)
    {
        if (Status == ProjectStatus.Completed)
        {
            return false;
        }

        Status = ProjectStatus.Completed;
        CompletedAt = completedAt;
        return true;
    }
}
