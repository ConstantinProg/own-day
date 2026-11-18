using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace OwnDay.Domain.Learning;

[JsonConverter(typeof(JsonStringEnumConverter<RubricCriterionResult>))]
public enum RubricCriterionResult { Unclear, Met, NotMet }
public enum ExerciseEvidenceKind { Understanding, Application, Retrieval }

public sealed record ExerciseCriterion(string Code, string Requirement);

public sealed record ExerciseRubric(int Version, ExerciseEvidenceKind Kind, ImmutableArray<ExerciseCriterion> Criteria)
{
    public void Validate()
    {
        if (Version != 1 || !Enum.IsDefined(Kind) || Criteria.IsDefaultOrEmpty || Criteria.Length > 5 ||
            Criteria.Any(criterion => criterion is null) ||
            Criteria.Select(criterion => criterion.Code).Distinct(StringComparer.Ordinal).Count() != Criteria.Length)
            throw new ArgumentException("Invalid exercise rubric.");
        foreach (var criterion in Criteria)
        {
            LearningProgram.Validate(criterion.Code, 40);
            LearningProgram.Validate(criterion.Requirement, 200);
        }
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
    string? Clarification, bool Final, RubricAssessment? InitialAssessment = null, Guid? InitialOperationId = null);
