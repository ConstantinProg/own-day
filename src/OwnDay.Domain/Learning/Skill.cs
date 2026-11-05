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
        LearningProgram.Validate(title, 500);
        description ??= title;
        LearningProgram.Validate(description, 1200);
        return new Skill { Id = Guid.NewGuid(), UserId = user, Title = title, Description = description };
    }
}
