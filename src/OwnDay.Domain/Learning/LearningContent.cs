namespace OwnDay.Domain.Learning;

public enum AnswerAssistance { Unknown, Independent, Assisted }
public enum LearningContentKind { ReadingChoice, OpenWriting }

public sealed record LearningContent(string Id, string Version, LearningContentKind Kind,
    string Objective, string Criterion, string Rubric)
{
    public void Validate()
    {
        LearningProgram.Validate(Id, 100);
        LearningProgram.Validate(Version, 50);
        LearningProgram.Validate(Objective, 500);
        LearningProgram.Validate(Criterion, 500);
        LearningProgram.Validate(Rubric, 1000);
        if (!Enum.IsDefined(Kind)) throw new ArgumentException("Unknown content kind.");
    }
}
