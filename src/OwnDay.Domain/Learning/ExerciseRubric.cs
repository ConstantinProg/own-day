using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace OwnDay.Domain.Learning;

[JsonConverter(typeof(JsonStringEnumConverter<RubricCriterionResult>))]
public enum RubricCriterionResult { Unclear, Met, NotMet }
public enum ExerciseEvidenceKind { Understanding, Application, Retrieval, Recognition }
[JsonConverter(typeof(JsonStringEnumConverter<ExerciseCorrectionKind>))]
public enum ExerciseCorrectionKind { Hint, Simplify, Example, AlternativeExplanation, PrerequisiteCheck, Redirect }

public sealed record ExerciseCriterion(string Code, string Requirement);

public sealed record ExerciseRubric(int Version, ExerciseEvidenceKind Kind, ImmutableArray<ExerciseCriterion> Criteria)
{
    public const int QuestionScopedVersion = 2;

    public void Validate()
    {
        if (Version is not (1 or QuestionScopedVersion) || !Enum.IsDefined(Kind) || Criteria.IsDefaultOrEmpty || Criteria.Length > 5 ||
            Criteria.Any(criterion => criterion is null) ||
            Criteria.Select(criterion => criterion.Code).Distinct(StringComparer.Ordinal).Count() != Criteria.Length)
            throw new ArgumentException("Invalid exercise rubric.");
        foreach (var criterion in Criteria)
        {
            LearningProgram.Validate(criterion.Code, 40);
            LearningProgram.Validate(criterion.Requirement, 200);
        }
        if (Version == QuestionScopedVersion &&
            (Criteria.Any(criterion => criterion.Requirement != criterion.Requirement.Trim() ||
                criterion.Requirement.Contains('\n') || criterion.Requirement.Contains('\r')) ||
                Criteria.Select(criterion => criterion.Requirement).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Criteria.Length ||
                QuestionText().Length > 1000))
            throw new ArgumentException("Question tasks must be distinct single lines within the question length limit.");
    }

    public string QuestionText() => string.Join('\n', Criteria.Select(criterion => criterion.Requirement));

    public void ValidateQuestion(string question)
    {
        if (Version == QuestionScopedVersion && question != QuestionText())
            throw new ArgumentException("The question must publish every rubric requirement as a task.");
    }
}

public sealed record RubricCriterionAssessment(string Code, RubricCriterionResult Result, string Reason);

public sealed record RubricAssessment(ImmutableArray<RubricCriterionAssessment> Criteria)
{
    public void Validate(ExerciseRubric rubric)
    {
        if (Criteria.IsDefaultOrEmpty || Criteria.Length != rubric.Criteria.Length ||
            Criteria.Any(assessment => assessment is null || !Enum.IsDefined(assessment.Result)) ||
            !rubric.Criteria.Select(criterion => criterion.Code).ToHashSet(StringComparer.Ordinal)
                .SetEquals(Criteria.Select(assessment => assessment.Code)))
            throw new ArgumentException("Assessment does not match the published rubric.");
        foreach (var assessment in Criteria) LearningProgram.Validate(assessment.Reason, 300);
    }
}

public sealed record LessonRubricEvaluation(RubricAssessment Assessment, string InitialAnswer, Guid? OperationId,
    string? Clarification, bool Final, RubricAssessment? InitialAssessment = null, Guid? InitialOperationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<ExerciseCorrection> Corrections = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool AwaitingCorrection = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClarificationAnswer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool FinalThroughClarification = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ScopedToQuestion = false);

public sealed record ExerciseCorrectionProposal(ExerciseCorrectionKind Kind, string Guidance)
{
    [JsonIgnore]
    public bool IsInstructional => Enum.IsDefined(Kind) && Kind != ExerciseCorrectionKind.Redirect &&
        !string.IsNullOrWhiteSpace(Guidance) && Guidance.Length <= 1000;
}

public sealed record ExerciseCorrection(string Answer, RubricAssessment Assessment, Guid? OperationId,
    ExerciseCorrectionProposal Proposal);
