using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class ReviewScheduleTests
{
    private static readonly DateTimeOffset _start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void MarkAsReview_UnlinkedOrEndedLesson_RejectsInvalidTransition()
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        Assert.Throws<InvalidOperationException>(lesson.MarkAsReview);
        lesson.LinkToSkill(Skill.Create(lesson.UserId, "Topic"));
        lesson.MarkAsReview();
        Assert.Throws<InvalidOperationException>(lesson.MarkAsReview);
        var program = LearningProgram.Create(lesson.UserId, "Goal", "Beginner", null, "Math", [new("topic", "Topic", [])], null);
        Assert.Throws<ArgumentException>(() => lesson.LinkToProgram(program, "topic"));
        lesson.Finish();
        Assert.Throws<InvalidOperationException>(lesson.MarkAsReview);
    }

    [Theory]
    [InlineData(true, 24)]
    [InlineData(false, 1)]
    public void FromAnswer_FirstAssessment_SetsDeterministicUtcDeadline(bool correct, int hours)
    {
        var skill = Skill.Create(new UserId(1), "Fractions");
        var evidence = Answer(skill, _start, correct);
        var schedule = ReviewSchedule.FromAnswer(evidence);
        Assert.Equal(_start.AddHours(hours), schedule.DueAt);
        Assert.False(schedule.IsDue(schedule.DueAt.AddTicks(-1)));
        Assert.True(schedule.IsDue(schedule.DueAt));
        schedule.Record(evidence);
        Assert.Equal(_start.AddHours(hours), schedule.DueAt);
        Assert.Equal(0, schedule.SuccessfulReviews);
    }

    [Fact]
    public void Record_SuccessiveDelayedReviews_ExpandsIntervalsAndCapsAtThirtyDays()
    {
        var skill = Skill.Create(new UserId(1), "Fractions");
        var schedule = ReviewSchedule.FromAnswer(Answer(skill, _start, true));
        foreach (var days in new[] { 3, 7, 14, 30, 30 })
        {
            var observed = schedule.DueAt.AddDays(20); // A late review creates one next deadline, without catch-up work.
            var evidence = Answer(skill, observed, true, review: true);
            schedule.Record(evidence);
            Assert.Equal(observed.AddDays(days), schedule.DueAt);
            schedule.Record(evidence);
            Assert.Equal(observed.AddDays(days), schedule.DueAt);
        }
        Assert.Equal(4, schedule.SuccessfulReviews);
    }

    [Fact]
    public void Record_EarlyPracticeOrEarlyReview_DoesNotExtendIntervalOrClaimRetention()
    {
        var skill = Skill.Create(new UserId(1), "Fractions");
        var schedule = ReviewSchedule.FromAnswer(Answer(skill, _start, true));
        schedule.Record(Answer(skill, _start.AddHours(1), true));
        schedule.Record(Answer(skill, _start.AddHours(2), true, review: true));
        Assert.Equal(_start.AddDays(1), schedule.DueAt);
        Assert.Equal(0, schedule.SuccessfulReviews);
        schedule.Record(Answer(skill, _start.AddDays(2), true));
        Assert.Equal(_start.AddDays(1), schedule.DueAt);
    }

    [Fact]
    public void Record_FailedReview_ResetsIntervalAndKeepsContradictoryKnowledgeUncertain()
    {
        var skill = Skill.Create(new UserId(1), "Fractions");
        var first = Answer(skill, _start, true);
        var schedule = ReviewSchedule.FromAnswer(first);
        var success = Answer(skill, schedule.DueAt, true, review: true);
        schedule.Record(success);
        var failure = Answer(skill, schedule.DueAt, false, review: true);
        schedule.Record(failure);
        Assert.Equal(0, schedule.SuccessfulReviews);
        Assert.Equal(failure.ObservedAt.AddHours(1), schedule.DueAt);
        Assert.Equal(SkillMastery.Conflicting, LearnerSkillState.Evaluate([first, success, failure]).Mastery);
        Assert.True(failure.IsReview);
        Assert.Equal(skill.Id, failure.SkillId);
    }

    [Theory]
    [InlineData(ReviewDeferral.Postpone, 1)]
    [InlineData(ReviewDeferral.Skip, 7)]
    public void Defer_DueReview_MovesSingleDeadlineWithoutChangingEvidenceOrStreak(ReviewDeferral deferral, int days)
    {
        var skill = Skill.Create(new UserId(1), "Fractions");
        var first = Answer(skill, _start, true);
        var schedule = ReviewSchedule.FromAnswer(first);
        var now = schedule.DueAt.AddDays(100);
        schedule.Defer(now, deferral);
        Assert.Equal(now.AddDays(days), schedule.DueAt);
        Assert.Equal(first.LessonId, schedule.LastLessonId);
        Assert.Equal(first.ObservedAt, schedule.LastObservedAt);
        Assert.Equal(0, schedule.SuccessfulReviews);
        Assert.Throws<InvalidOperationException>(() => schedule.Defer(now, deferral));
        Assert.Throws<ArgumentException>(() => schedule.Defer(now.ToOffset(TimeSpan.FromHours(3)), deferral));
    }

    [Fact]
    public void Record_ForeignOrOlderEvidence_RejectsWithoutChangingDeadline()
    {
        var skill = Skill.Create(new UserId(1), "Fractions");
        var schedule = ReviewSchedule.FromAnswer(Answer(skill, _start, true));
        Assert.Throws<ArgumentException>(() => schedule.Record(Answer(Skill.Create(new UserId(2), "Fractions"), _start, true)));
        Assert.Throws<ArgumentException>(() => schedule.Record(Answer(Skill.Create(new UserId(1), "Other"), _start, true)));
        Assert.Throws<ArgumentException>(() => schedule.Record(Answer(skill, _start.AddDays(-1), true)));
        Assert.Equal(_start.AddDays(1), schedule.DueAt);
    }

    internal static LearningEvidence Answer(Skill skill, DateTimeOffset at, bool correct, bool review = false)
    {
        var lesson = Lesson.Start(skill.UserId, skill.Title, "Context", "A new application question");
        lesson.LinkToSkill(skill);
        if (review) lesson.MarkAsReview();
        lesson.Complete("Answer", "AI hypothesis", correct);
        return LearningEvidence.FromAnswer(lesson, at, "test-model");
    }
}
