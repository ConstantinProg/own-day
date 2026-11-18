using System.Collections.Immutable;

namespace OwnDay.Domain.Learning;

public enum AssessmentReviewOutcome { Confirmed, Changed, Unresolved }

public sealed record RubricReviewCheck(RubricAssessment Assessment, string Rationale, string Source);

public sealed record AssessmentReviewRevision(int Number, Guid OperationId, string Argument, DateTimeOffset ReviewedAt,
    AssessmentReviewOutcome Outcome, RubricReviewCheck Primary, RubricReviewCheck? Verification);

public sealed record AssessmentReviewHistory(ImmutableArray<AssessmentReviewRevision> Revisions);

public sealed partial class LearningEvidence
{
    public AssessmentReviewHistory? ReviewHistory { get; private set; }
    public bool? ReassessmentCredit { get; private set; }
    public int ReviewVersion => ReviewHistory?.Revisions.Length ?? 0;
    public bool EffectiveCorrect => ReassessmentCredit ?? AssessedCorrect;
    public AnswerAssessmentStatus EffectiveAssessmentStatus => ReassessmentCredit is not bool corrected
        ? AssessmentStatus
        : DeterministicIsCorrect == corrected ? AnswerAssessmentStatus.Agreement : AnswerAssessmentStatus.AiOnly;

    public bool CanReview(int expectedVersion, string argument) =>
        EffectiveRubric is not null && expectedVersion == ReviewVersion &&
        !string.IsNullOrWhiteSpace(argument) && argument.Length <= 1000 &&
        (ReviewHistory is null || !ReviewHistory.Revisions.Any(item =>
            string.Equals(NormalizedTextAnswerCheck.Normalize(item.Argument), NormalizedTextAnswerCheck.Normalize(argument),
                StringComparison.OrdinalIgnoreCase)));

    public bool NeedsIndependentCheck(RubricReviewCheck primary)
    {
        ValidateCheck(primary);
        return Credit(primary) is bool proposed && ChangesResult(proposed) && DeterministicIsCorrect is null;
    }

    public AssessmentReviewRevision Review(int expectedVersion, Guid operationId, string argument, DateTimeOffset at,
        RubricReviewCheck primary, RubricReviewCheck? verification)
    {
        if (!CanReview(expectedVersion, argument) || operationId == Guid.Empty || at < ObservedAt ||
            ReviewHistory?.Revisions.Any(item => item.OperationId == operationId) == true)
            throw new InvalidOperationException("The assessment review is stale or already recorded.");
        ValidateCheck(primary);
        if (verification is not null) ValidateCheck(verification);
        var proposed = Credit(primary);
        var outcome = AssessmentReviewOutcome.Unresolved;
        if (proposed is bool credit)
        {
            if (!ChangesResult(credit)) outcome = AssessmentReviewOutcome.Confirmed;
            else if ((DeterministicIsCorrect is not bool reference || reference == credit) &&
                (!NeedsIndependentCheck(primary) || verification is not null && Credit(verification) == credit))
                outcome = AssessmentReviewOutcome.Changed;
        }
        var revision = new AssessmentReviewRevision(expectedVersion + 1, operationId, argument, at.ToUniversalTime(),
            outcome, primary, verification);
        ReviewHistory = new((ReviewHistory?.Revisions ?? []).Add(revision));
        if (outcome == AssessmentReviewOutcome.Changed) ReassessmentCredit = proposed;
        return revision;
    }

    private ExerciseRubric? EffectiveRubric => RubricEvidence is null ? null : Content?.StructuredRubric;

    private bool ChangesResult(bool proposed) => EffectiveCorrect != proposed ||
        EffectiveAssessmentStatus is AnswerAssessmentStatus.Inconclusive or AnswerAssessmentStatus.Conflict;

    private static bool? Credit(RubricReviewCheck review) => review.Assessment.Criteria.Any(item => item.Result == RubricCriterionResult.Unclear)
        ? null : review.Assessment.Criteria.All(item => item.Result == RubricCriterionResult.Met);

    private void ValidateCheck(RubricReviewCheck check)
    {
        check.Assessment.Validate(EffectiveRubric ?? throw new InvalidOperationException("No saved rubric."));
        LearningProgram.Validate(check.Rationale, 3000);
        LearningProgram.Validate(check.Source, 200);
    }
}
