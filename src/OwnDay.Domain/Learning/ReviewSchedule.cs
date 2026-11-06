namespace OwnDay.Domain.Learning;

public enum ReviewDeferral
{
    Postpone,
    Skip
}

public sealed class ReviewSchedule
{
    private static readonly int[] _successfulReviewDays = [3, 7, 14, 30];
    private ReviewSchedule() { }

    public Guid SkillId { get; private set; }
    public UserId UserId { get; private set; }
    public DateTimeOffset DueAt { get; private set; }
    public DateTimeOffset LastObservedAt { get; private set; }
    public Guid LastLessonId { get; private set; }
    public int SuccessfulReviews { get; private set; }

    public static ReviewSchedule FromAnswer(LearningEvidence evidence)
    {
        var schedule = new ReviewSchedule { SkillId = evidence.SkillId, UserId = evidence.UserId };
        schedule.Record(evidence);
        return schedule;
    }

    public bool IsDue(DateTimeOffset now) => DueAt <= now;

    public void Record(LearningEvidence evidence)
    {
        if (evidence.SkillId != SkillId || evidence.UserId != UserId)
            throw new ArgumentException("Evidence belongs to another learner or skill.", nameof(evidence));
        if (LastLessonId == evidence.LessonId) return;
        if (LastLessonId != Guid.Empty && evidence.ObservedAt < LastObservedAt)
            throw new ArgumentException("Evidence must not precede the last observation.", nameof(evidence));

        if (!evidence.AssessedCorrect)
        {
            SuccessfulReviews = 0;
            DueAt = evidence.ObservedAt.AddHours(1);
        }
        else if (evidence.IsReview && LastLessonId != Guid.Empty && IsDue(evidence.ObservedAt))
        {
            SuccessfulReviews = Math.Min(SuccessfulReviews + 1, _successfulReviewDays.Length);
            DueAt = evidence.ObservedAt.AddDays(_successfulReviewDays[SuccessfulReviews - 1]);
        }
        else if (LastLessonId == Guid.Empty)
        {
            DueAt = evidence.ObservedAt.AddDays(1);
        }
        // Early practice never postpones an existing review or proves retention.
        LastObservedAt = evidence.ObservedAt;
        LastLessonId = evidence.LessonId;
    }

    public void Defer(DateTimeOffset now, ReviewDeferral deferral)
    {
        var days = deferral switch
        {
            ReviewDeferral.Postpone => 1,
            ReviewDeferral.Skip => 7,
            _ => throw new ArgumentOutOfRangeException(nameof(deferral))
        };
        if (now.Offset != TimeSpan.Zero) throw new ArgumentException("UTC timestamp required.", nameof(now));
        if (!IsDue(now)) throw new InvalidOperationException("Review is not due.");
        DueAt = now.AddDays(days);
    }
}
