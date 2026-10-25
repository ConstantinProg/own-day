using OwnDay.Domain;

namespace OwnDay.Domain.Actions;

public sealed class Action
{
    public const int MaxTitleLength = 200;

    private Action() { }

    private Action(UserId userId, string title, DateTime createdAt, long? projectId)
    {
        UserId = userId;
        Title = title;
        CreatedAt = createdAt;
        Status = ActionStatus.Active;
        ProjectId = projectId;
    }

    public long Id { get; private set; }
    public UserId UserId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public ActionStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public long? ProjectId { get; private set; }

    public static Action Create(UserId userId, string title, DateTime createdAt, long? projectId = null)
    {
        if (!userId.IsValid)
        {
            throw new ArgumentException("A valid user is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitleLength)
        {
            throw new ArgumentException("Title must contain 1 to 200 characters.", nameof(title));
        }

        if (projectId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(projectId));
        }

        DomainTime.RequireUtc(createdAt, nameof(createdAt));
        return new Action(userId, title.Trim(), createdAt, projectId);
    }

    public bool Complete(DateTime completedAt)
    {
        if (Status == ActionStatus.Completed)
        {
            return false;
        }

        DomainTime.RequireNotBefore(completedAt, CreatedAt, nameof(completedAt));

        Status = ActionStatus.Completed;
        CompletedAt = completedAt;
        return true;
    }
}
