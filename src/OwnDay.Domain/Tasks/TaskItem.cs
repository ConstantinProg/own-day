namespace OwnDay.Domain.Tasks;

public sealed class TaskItem
{
    public const int MaxTitleLength = 200;

    private TaskItem() { }

    private TaskItem(UserId userId, string title, DateTime createdAt)
    {
        UserId = userId;
        Title = title;
        CreatedAt = createdAt;
        Status = TaskItemStatus.Active;
    }

    public long Id { get; private set; }
    public UserId UserId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public TaskItemStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    public static TaskItem Create(UserId userId, string title, DateTime createdAt)
    {
        if (!userId.IsValid)
        {
            throw new ArgumentException("A valid user is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitleLength)
        {
            throw new ArgumentException("Title must contain 1 to 200 characters.", nameof(title));
        }

        return new TaskItem(userId, title.Trim(), createdAt);
    }

    public bool Complete(DateTime completedAt)
    {
        if (Status == TaskItemStatus.Completed)
        {
            return false;
        }

        Status = TaskItemStatus.Completed;
        CompletedAt = completedAt;
        return true;
    }
}
