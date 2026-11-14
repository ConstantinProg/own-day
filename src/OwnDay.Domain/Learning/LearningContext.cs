namespace OwnDay.Domain.Learning;

public sealed partial class LearningContext
{
    private LearningContext() { }
    public UserId UserId { get; private set; }
    public Guid ProgramId { get; private set; }

    public static LearningContext Create(LearningProgram program) => new() { UserId = program.UserId, ProgramId = program.Id };

    public void Select(LearningProgram program)
    {
        if (program.UserId != UserId) throw new ArgumentException("Invalid program owner.", nameof(program));
        if (Practice is { Supported: false }) throw new InvalidOperationException("A compatible practice reader is required.");
        if (program.Id != ProgramId && Practice is { Active: true }) throw new InvalidOperationException("End the current practice before switching programs.");
        if (program.Id != ProgramId) Practice = null;
        ProgramId = program.Id;
    }
}
