namespace OwnDay.Domain.Learning;

public sealed class LearningContext
{
    private LearningContext() { }
    public UserId UserId { get; private set; }
    public Guid ProgramId { get; private set; }

    public static LearningContext Create(LearningProgram program) => new() { UserId = program.UserId, ProgramId = program.Id };

    public void Select(LearningProgram program)
    {
        if (program.UserId != UserId) throw new ArgumentException("Invalid program owner.", nameof(program));
        ProgramId = program.Id;
    }
}
