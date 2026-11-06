using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LessonVerificationTests
{
    [Theory]
    [InlineData("  NEW\n  York ", "new york", true)]
    [InlineData("a+b", "a + b", false)]
    [InlineData("2.0", "2", false)]
    [InlineData("run dangerous code", "four", false)]
    public void Check_NormalizedText_ComparesOnlyCaseAndWhitespace(string answer, string expected, bool matches)
    {
        IAnswerCheck check = new NormalizedTextAnswerCheck();
        Assert.Equal(matches, check.Check(answer, expected));
    }

    [Theory]
    [InlineData(true, "four", AnswerAssessmentStatus.Agreement, true)]
    [InlineData(false, "five", AnswerAssessmentStatus.Agreement, false)]
    [InlineData(true, "five", AnswerAssessmentStatus.Conflict, false)]
    [InlineData(false, "four", AnswerAssessmentStatus.Conflict, false)]
    public void Complete_ReferenceAndAiAssessment_ReconcilesWithoutCreditingConflict(bool aiCorrect, string answer,
        AnswerAssessmentStatus status, bool credited)
    {
        var lesson = Answer(answer, aiCorrect);
        Assert.Equal(status, lesson.AssessmentStatus);
        Assert.Equal(aiCorrect, lesson.AiIsCorrect);
        Assert.Equal(answer == "four", lesson.DeterministicIsCorrect);
        Assert.Equal(credited, lesson.IsCorrect);
        var evidence = LearningEvidence.FromAnswer(lesson, DateTimeOffset.UtcNow, "fake model");
        Assert.Equal(status, evidence.AssessmentStatus);
        Assert.Equal(aiCorrect, evidence.AiAssessedCorrect);
        Assert.Equal(lesson.DeterministicIsCorrect, evidence.DeterministicIsCorrect);
        Assert.Equal("AI + NormalizedText/v1 (AI reference)", evidence.Method);
        var state = LearnerSkillState.Evaluate([evidence]);
        Assert.Equal(status == AnswerAssessmentStatus.Conflict ? SkillMastery.Conflicting
            : credited ? SkillMastery.Developing : SkillMastery.NeedsPractice, state.Mastery);
        if (status == AnswerAssessmentStatus.Conflict)
        {
            Assert.Equal(0, state.PositiveAssessments);
            Assert.Equal(0, state.NegativeAssessments);
        }
    }

    [Fact]
    public void Record_ConflictingReview_DoesNotEarnLongerIntervalOrEraseSuccessCount()
    {
        var now = DateTimeOffset.UtcNow;
        var user = new UserId(1);
        var skill = Skill.Create(user, "Numbers", "Numbers");
        // Use a single identity for all observations in this schedule.
        var initial = Lesson.Start(user, "Numbers", "Theory", "Question", expectedAnswer: "four");
        initial.LinkToSkill(skill);
        initial.Complete("four", "Feedback", true);
        var schedule = ReviewSchedule.FromAnswer(LearningEvidence.FromAnswer(initial, now, "model"));
        var success = Lesson.Start(user, "Numbers", "Theory", "Question", expectedAnswer: "four");
        success.LinkToSkill(skill);
        success.MarkAsReview();
        success.Complete("four", "AI says correct", true);
        schedule.Record(LearningEvidence.FromAnswer(success, now.AddDays(1), "model"));
        Assert.Equal(1, schedule.SuccessfulReviews);
        var review = Lesson.Start(user, "Numbers", "Theory", "Question", expectedAnswer: "four");
        review.LinkToSkill(skill);
        review.MarkAsReview();
        review.Complete("five", "AI says correct", true);
        schedule.Record(LearningEvidence.FromAnswer(review, now.AddDays(4), "model"));
        Assert.Equal(now.AddDays(4), schedule.DueAt);
        Assert.Equal(1, schedule.SuccessfulReviews);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("oversized")]
    public void Start_InvalidReference_RejectsProposal(string expected)
    {
        if (expected == "oversized") expected = new string('a', 501);
        Assert.Throws<ArgumentException>(() => Lesson.Start(new UserId(1), "Topic", "Theory", "Question", expectedAnswer: expected));
    }

    [Fact]
    public void Complete_LegacyLesson_RetainsAiOnlyBehavior()
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        lesson.Complete("Answer", "Feedback", true);
        Assert.True(lesson.IsCorrect);
        Assert.Null(lesson.DeterministicIsCorrect);
        Assert.Equal(AnswerAssessmentStatus.AiOnly, lesson.AssessmentStatus);
    }

    private static Lesson Answer(string answer, bool correct)
    {
        var lesson = Lesson.Start(new UserId(1), "Numbers", "Theory", "Question", expectedAnswer: "four");
        lesson.LinkToSkill(Skill.Create(lesson.UserId, "Numbers", "Numbers"));
        lesson.Complete(answer, "AI feedback", correct);
        return lesson;
    }
}
