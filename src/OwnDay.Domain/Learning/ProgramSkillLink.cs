namespace OwnDay.Domain.Learning;

public sealed class ProgramSkillLink
{
    public const int MaxReasonLength = 500;
    private ProgramSkillLink() { }
    public Guid ProgramId { get; private set; }
    public UserId UserId { get; private set; }
    public int ProgressVersion { get; private set; }
    public string TopicCode { get; private set; } = "";
    public Guid SkillId { get; private set; }
    public string Reason { get; private set; } = "";
    public DateTimeOffset ConfirmedAt { get; private set; }
    public string Source { get; private set; } = "User confirmation";
    public string Trust { get; private set; } = "UserConfirmedIdentity";

    public static ProgramSkillLink Confirm(LearningProgram program, string topicCode, Skill skill, string reason, DateTimeOffset now)
    {
        if (program.UserId != skill.UserId || program.DiagnosticPending ||
            !program.CurrentRoute.Topics.Any(t => t.Code == topicCode))
            throw new ArgumentException("Invalid shared skill topic or owner.");
        LearningProgram.Validate(reason, MaxReasonLength);
        return new ProgramSkillLink
        {
            ProgramId = program.Id, UserId = program.UserId,
            ProgressVersion = program.CurrentRoute.ProgressVersion ?? program.CurrentRoute.Version,
            TopicCode = topicCode, SkillId = skill.Id, Reason = reason, ConfirmedAt = now.ToUniversalTime()
        };
    }
}
