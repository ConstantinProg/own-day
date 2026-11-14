using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class AssistedEvidenceTests
{
    [Fact]
    public void Evaluate_ThreeSuccessfulAnswersAfterHelp_DoesNotCreditIndependentMastery()
    {
        var skill = Skill.Create(new UserId(1), "Reading requests");
        var evidence = Enumerable.Range(0, 3).Select(_ => AssistedAnswer(skill, DateTimeOffset.UnixEpoch)).ToArray();

        var state = LearnerSkillState.Evaluate(evidence);

        Assert.Equal(3, state.Answers);
        Assert.Equal(0, state.PositiveAssessments);
        Assert.Equal(SkillMastery.Unknown, state.Mastery);
        Assert.All(evidence, item => Assert.True(item.AssessedCorrect));
    }

    [Fact]
    public void Record_AssistedDelayedReview_DoesNotEarnLongerInterval()
    {
        var skill = Skill.Create(new UserId(1), "Reading requests");
        var first = LearnerSkillStateTests.Answer(skill, true);
        var schedule = ReviewSchedule.FromAnswer(first);
        var due = schedule.DueAt;

        schedule.Record(AssistedAnswer(skill, due));

        Assert.Equal(0, schedule.SuccessfulReviews);
        Assert.Equal(due, schedule.DueAt);
    }

    private static LearningEvidence AssistedAnswer(Skill skill, DateTimeOffset observedAt)
    {
        var lesson = Lesson.Start(skill.UserId, skill.Title, "Read the email", "What is requested?");
        lesson.LinkToSkill(skill);
        lesson.MarkAsReview();
        lesson.AddExplanation(LessonInteractionKind.Clarification, "Explain please", "Find the request after could.");
        lesson.Pause();
        lesson.Resume();
        lesson.Complete("Send the file", "Correct according to the rubric", true);
        return LearningEvidence.FromAnswer(lesson, observedAt, "test-model");
    }
}
