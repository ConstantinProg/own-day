namespace OwnDay.Domain.Learning;

// Server-selected provenance of a confirmed identity shared across two disciplines.
public sealed record InterdisciplinaryPractice(Guid SourceProgramId, int SourceRouteVersion, string SourceTopicCode,
    string SourceDiscipline, string TargetDiscipline, Guid SkillId, string Reason, string Source)
{
    public static InterdisciplinaryPractice Create(LearningProgram target, LearningProgram source, string sourceCode, ProgramSkillLink link)
    {
        if (source.UserId != target.UserId || source.Id == target.Id || source.DiagnosticPending || target.DiagnosticPending ||
            string.Equals(source.Discipline, target.Discipline, StringComparison.OrdinalIgnoreCase) ||
            !source.CurrentRoute.Topics.Any(t => t.Code == sourceCode) || link.UserId != target.UserId ||
            link.ProgramId != target.Id || link.ProgressVersion != target.CurrentRoute.ProgressRootVersion ||
            !target.CurrentRoute.Topics.Any(t => t.Code == link.TopicCode))
            throw new ArgumentException("Transfer requires a confirmed shared identity across disciplines.");
        return new(source.Id, source.CurrentRoute.Version, sourceCode, source.Discipline, target.Discipline,
            link.SkillId, link.Reason, link.Source);
    }
}
