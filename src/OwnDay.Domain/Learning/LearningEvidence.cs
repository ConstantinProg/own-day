using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace OwnDay.Domain.Learning;

// The answer is an observation. Correctness and feedback are a provider's interpretation.
public sealed partial class LearningEvidence
{
    private LearningEvidence() { }
    public Guid LessonId { get; private set; }
    public Guid SkillId { get; private set; }
    public UserId UserId { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; }
    public string Answer { get; private set; } = "";
    public string AssessmentFeedback { get; private set; } = "";
    public bool AssessedCorrect { get; private set; }
    public bool? AiAssessedCorrect { get; private set; }
    public bool? DeterministicIsCorrect { get; private set; }
    public AnswerAssessmentStatus AssessmentStatus { get; private set; }
    public string AssessmentSource { get; private set; } = "";
    public string Method { get; private set; } = "AI answer assessment";
    public bool IsReview { get; private set; }
    public AnswerAssistance Assistance { get; private set; }
    public LearningContent? Content { get; private set; }
    public RubricEvidence? RubricEvidence { get; private set; }

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
            AssessmentSource = source, IsReview = lesson.IsReview, Assistance = lesson.Assistance, Content = lesson.AssessmentContent,
            AiAssessedCorrect = lesson.AssessmentStatus == AnswerAssessmentStatus.Inconclusive ? null : lesson.AiIsCorrect ?? lesson.IsCorrect,
            DeterministicIsCorrect = lesson.DeterministicIsCorrect, AssessmentStatus = lesson.AssessmentStatus,
            RubricEvidence = lesson.RubricEvaluation is { Final: true } rubric
                ? new(rubric, RubricEvidence.FingerprintQuestion(lesson.Question))
                : null,
            Method = lesson.UsesReferenceCheck ? $"AI + {NormalizedTextAnswerCheck.MethodName} (AI reference)"
                : lesson.RubricEvaluation is not null ? "AI criterion assessment" : "AI answer assessment"
        };
    }
}

public sealed record RubricEvidence(LessonRubricEvaluation Evaluation, string QuestionFingerprint)
{
    public static string FingerprintQuestion(string question) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizedTextAnswerCheck.Normalize(question).ToUpperInvariant())));
}
public sealed record MasteryEvidenceSummary(bool Understanding, bool Application, bool Retention);

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MasteryEvidenceSummary? EvidenceSummary { get; init; }

    public bool RepeatedDifficulty => NegativeAssessments >= 2;

    public static LearnerSkillState Evaluate(IEnumerable<LearningEvidence> evidence)
    {
        var observations = evidence.DistinctBy(e => e.LessonId).ToArray();
        if (observations.Select(e => (e.UserId, e.SkillId)).Distinct().Count() > 1)
            throw new ArgumentException("Evidence must belong to one learner and skill.", nameof(evidence));
        var positive = observations.Count(e => e.EffectiveCorrect && e.Assistance == AnswerAssistance.Independent &&
            e.EffectiveAssessmentStatus != AnswerAssessmentStatus.Conflict);
        var negative = observations.Count(e => !e.EffectiveCorrect && e.EffectiveAssessmentStatus is not
            (AnswerAssessmentStatus.Conflict or AnswerAssessmentStatus.Inconclusive));
        var summary = observations.Any(e => e.RubricEvidence is not null) ? SummarizeRubricEvidence(observations) : null;
        var demonstrated = summary is null ? positive >= 3 : summary is { Understanding: true, Application: true, Retention: true };
        var mastery = observations.Length == 0 ? SkillMastery.Unknown
            : observations.Any(e => e.EffectiveAssessmentStatus == AnswerAssessmentStatus.Conflict) ? SkillMastery.Conflicting
            : negative > 0 && positive > 0 ? SkillMastery.Conflicting
            : negative > 0 ? SkillMastery.NeedsPractice
            : demonstrated ? SkillMastery.Demonstrated : positive > 0 ? SkillMastery.Developing : SkillMastery.Unknown;
        return new(mastery, observations.Length, positive, negative) { EvidenceSummary = summary };
    }

    private static MasteryEvidenceSummary SummarizeRubricEvidence(IEnumerable<LearningEvidence> observations)
    {
        var eligible = IndependentRubricEvidence(observations);
        var understanding = eligible.Count(e => e.Content!.StructuredRubric!.Kind == ExerciseEvidenceKind.Understanding) >= 2;
        var application = eligible.Any(e => e.Content!.StructuredRubric!.Kind == ExerciseEvidenceKind.Application);
        var retention = eligible.Any(review => IsDelayedRetrieval(eligible, review));
        return new(understanding, application, retention);
    }

    public static bool HasRecentRetention(IEnumerable<LearningEvidence> observations, DateTimeOffset now, TimeSpan maxAge)
    {
        var eligible = IndependentRubricEvidence(observations.Where(evidence => evidence.ObservedAt <= now));
        return eligible.Any(review => now - review.ObservedAt <= maxAge && IsDelayedRetrieval(eligible, review));
    }

    private static LearningEvidence[] IndependentRubricEvidence(IEnumerable<LearningEvidence> observations) => observations
        .Where(e => e.EffectiveCorrect && e.Assistance == AnswerAssistance.Independent &&
            e.EffectiveAssessmentStatus != AnswerAssessmentStatus.Conflict &&
            e.RubricEvidence is { Evaluation.OperationId: not null } && e.Content?.StructuredRubric is not null)
        .OrderBy(e => e.ObservedAt)
        .DistinctBy(e => e.RubricEvidence!.QuestionFingerprint, StringComparer.Ordinal)
        .DistinctBy(e => NormalizedTextAnswerCheck.Normalize(e.Answer), StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool IsDelayedRetrieval(LearningEvidence[] eligible, LearningEvidence review) => review.IsReview &&
        review.Content!.StructuredRubric!.Kind == ExerciseEvidenceKind.Retrieval &&
        eligible.Any(earlier => !earlier.IsReview && review.ObservedAt - earlier.ObservedAt >= TimeSpan.FromDays(1));
}
