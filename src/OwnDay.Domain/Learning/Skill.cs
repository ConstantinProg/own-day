namespace OwnDay.Domain.Learning;

public sealed class Skill
{
    private Skill() { }
    public Guid Id { get; private set; }
    public UserId UserId { get; private set; }
    public string Title { get; private set; } = "";
    public string Description { get; private set; } = "";

    public static Skill Create(UserId user, string title, string? description = null)
    {
        if (!user.IsValid) throw new ArgumentException("Invalid user.", nameof(user));
        if (string.IsNullOrWhiteSpace(title) || title.Length > 500)
            throw new ArgumentException("Invalid skill title.", nameof(title));
        description ??= title;
        if (string.IsNullOrWhiteSpace(description) || description.Length > 1200)
            throw new ArgumentException("Invalid skill description.", nameof(description));
        return new Skill { Id = Guid.NewGuid(), UserId = user, Title = title, Description = description };
    }
}
