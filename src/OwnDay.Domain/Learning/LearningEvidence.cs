namespace OwnDay.Domain.Learning;

// The answer is an observation. Correctness and feedback are a provider's interpretation.
public sealed class LearningEvidence
{
    private LearningEvidence() { }
    public Guid LessonId { get; private set; }
    public Guid SkillId { get; private set; }
    public UserId UserId { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; }
    public string Answer { get; private set; } = "";
    public string AssessmentFeedback { get; private set; } = "";
    public bool AssessedCorrect { get; private set; }
    public string AssessmentSource { get; private set; } = "";
    public string Method { get; private set; } = "AI answer assessment";
    public bool IsReview { get; private set; }

    public static LearningEvidence FromAnswer(Lesson lesson, DateTimeOffset observedAt, string source)
    {
        if (lesson.Status != LessonStatus.Completed || lesson.SkillId is null)
            throw new InvalidOperationException("A completed answer with a skill is required.");
        if (string.IsNullOrWhiteSpace(source) || source.Length > 200)
            throw new ArgumentException("Invalid assessment source.", nameof(source));
        if (observedAt.Offset != TimeSpan.Zero) throw new ArgumentException("UTC timestamp required.", nameof(observedAt));
        return new LearningEvidence
        {
            LessonId = lesson.Id, SkillId = lesson.SkillId.Value, UserId = lesson.UserId, ObservedAt = observedAt,
            Answer = lesson.Answer!, AssessmentFeedback = lesson.Feedback!, AssessedCorrect = lesson.IsCorrect!.Value,
            AssessmentSource = source, IsReview = lesson.IsReview
        };
    }
}

public enum SkillMastery
{
    Unknown,
    NeedsPractice,
    Developing,
    Conflicting,
    Demonstrated
}

public sealed record LearnerSkillState(SkillMastery Mastery, int Answers, int PositiveAssessments, int NegativeAssessments)
{
    public bool RepeatedDifficulty => NegativeAssessments >= 2;

    public static LearnerSkillState Evaluate(IEnumerable<LearningEvidence> evidence)
    {
        var observations = evidence.DistinctBy(e => e.LessonId).ToArray();
        if (observations.Select(e => (e.UserId, e.SkillId)).Distinct().Count() > 1)
            throw new ArgumentException("Evidence must belong to one learner and skill.", nameof(evidence));
        var positive = observations.Count(e => e.AssessedCorrect);
        var negative = observations.Length - positive;
        var mastery = observations.Length == 0 ? SkillMastery.Unknown
            : negative > 0 && positive > 0 ? SkillMastery.Conflicting
            : negative > 0 ? SkillMastery.NeedsPractice
            : positive >= 3 ? SkillMastery.Demonstrated : SkillMastery.Developing;
        return new(mastery, observations.Length, positive, negative);
    }
}
