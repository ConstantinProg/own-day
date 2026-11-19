using OwnDay.Application.Learning.Requests;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public sealed record AssessmentAttempt(Lesson Lesson, LearningEvidence Evidence);

public interface IAssessmentReviewStore
{
    Task<AssessmentAttempt?> FindAsync(UserId user, Guid lessonId, CancellationToken token);
    Task<AssessmentAttempt?> FindLatestAsync(UserId user, Guid? programId, CancellationToken token);
}

public abstract record AssessmentReviewResult
{
    public sealed record Detail(AssessmentAttempt Attempt, AssessmentReviewRevision? Revision = null) : AssessmentReviewResult;
    public sealed record Unavailable : AssessmentReviewResult;
    public sealed record NotFound : AssessmentReviewResult;
    public sealed record Stale : AssessmentReviewResult;
}

public sealed class AssessmentReviewService(IAssessmentReviewStore attempts, IKnowledgeStore knowledge,
    ILessonTeacher teacher, TimeProvider clock)
{
    public async Task<AssessmentReviewResult> ExplainAsync(UserId user, Guid? programId, string input, CancellationToken token)
    {
        var attempt = !string.IsNullOrWhiteSpace(input)
            ? Guid.TryParse(input.Trim(), out var id) ? await attempts.FindAsync(user, id, token) : null
            : await attempts.FindLatestAsync(user, programId, token);
        return attempt is null ? new AssessmentReviewResult.NotFound() : new AssessmentReviewResult.Detail(attempt);
    }

    public async Task<AssessmentReviewResult> ReviewAsync(UserId user, Guid operationId, string input, CancellationToken token)
    {
        var parts = input.Split('|', 3, StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !Guid.TryParse(parts[0], out var lessonId) || !int.TryParse(parts[1], out var version))
            return new AssessmentReviewResult.Stale();
        var attempt = await attempts.FindAsync(user, lessonId, token);
        if (attempt is null) return new AssessmentReviewResult.NotFound();
        var evidence = attempt.Evidence;
        if (!evidence.CanReview(version, parts[2])) return new AssessmentReviewResult.Stale();

        RubricReviewCheck primary;
        RubricReviewCheck? verification;
        try
        {
            var snapshot = LessonSnapshot.From(attempt.Lesson) with
            {
                Review = new(parts[2], "Reassessment"),
                Content = evidence.Content,
                RubricEvaluation = null
            };
            var evaluation = evidence.RubricEvidence!.Evaluation;
            var hasClarification = evaluation.FinalThroughClarification ||
                evaluation.Clarification is not null && evaluation.Corrections.IsDefaultOrEmpty;
            var response = hasClarification
                ? $"Initial answer: {evaluation.ClarificationAnswer ?? evaluation.InitialAnswer}\nClarification: {evidence.Answer}"
                : evidence.Answer;
            primary = await CheckAsync(snapshot, response, token);
            verification = evidence.NeedsIndependentCheck(primary)
                ? await CheckAsync(snapshot with { Review = new(parts[2], "Independent verification") }, response, token)
                : null;
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (error is ArgumentException or LessonProviderException)
        {
            return new AssessmentReviewResult.Unavailable();
        }

        var history = await knowledge.EvidenceAsync(user, evidence.SkillId, token);
        var schedule = await knowledge.ReviewAsync(user, evidence.SkillId, token);
        var previous = ReviewSchedule.Reconstruct(history);
        var reviewedAt = clock.GetUtcNow();
        var revision = evidence.Review(version, operationId, parts[2],
            reviewedAt < evidence.ObservedAt ? evidence.ObservedAt : reviewedAt, primary, verification);
        if (revision.Outcome == AssessmentReviewOutcome.Changed)
        {
            var recalculated = ReviewSchedule.Reconstruct(history.Select(item => item.LessonId == evidence.LessonId ? evidence : item))!;
            if (schedule is null) knowledge.Add(recalculated);
            else schedule.AdoptRecalculation(recalculated, previous is not null && schedule.DueAt > previous.DueAt ? schedule.DueAt : null);
        }
        return new AssessmentReviewResult.Detail(attempt, revision);
    }

    private async Task<RubricReviewCheck> CheckAsync(LessonSnapshot lesson, string answer, CancellationToken token)
    {
        var result = await teacher.AssessAsync(lesson, answer, token);
        if (result.Rubric is null || string.IsNullOrWhiteSpace(result.Feedback) || result.Feedback.Length > 3000 ||
            string.IsNullOrWhiteSpace(result.Source) || result.Source.Length > 200)
            throw new LessonProviderException(LessonProviderFailure.InvalidResponse);
        result.Rubric.Validate(lesson.Content!.StructuredRubric!);
        return new(result.Rubric, result.Feedback, result.Source);
    }

}
