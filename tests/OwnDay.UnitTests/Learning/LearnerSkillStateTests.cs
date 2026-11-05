using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LearnerSkillStateTests
{
    [Theory]
    [InlineData("", SkillMastery.Unknown, false)]
    [InlineData("+", SkillMastery.Developing, false)]
    [InlineData("++", SkillMastery.Developing, false)]
    [InlineData("+++", SkillMastery.Demonstrated, false)]
    [InlineData("-", SkillMastery.NeedsPractice, false)]
    [InlineData("--", SkillMastery.NeedsPractice, true)]
    [InlineData("+-", SkillMastery.Conflicting, false)]
    [InlineData("-+++", SkillMastery.Conflicting, false)]
    [InlineData("++--", SkillMastery.Conflicting, true)]
    public void Evaluate_AssessedAnswers_UsesConservativeExplicitRules(string results, SkillMastery expected, bool repeated)
    {
        var skill = Skill.Create(new UserId(1), "Fractions");
        var evidence = results.Select(r => Answer(skill, r == '+')).ToArray();
        var state = LearnerSkillState.Evaluate(evidence);
        Assert.Equal(expected, state.Mastery);
        Assert.Equal(results.Length, state.Answers);
        Assert.Equal(results.Count(r => r == '+'), state.PositiveAssessments);
        Assert.Equal(repeated, state.RepeatedDifficulty);
        Assert.Equal(state, LearnerSkillState.Evaluate(evidence.Reverse()));
    }

    [Fact]
    public void Evaluate_DuplicateLesson_DoesNotIncreaseConfidence()
    {
        var evidence = Answer(Skill.Create(new UserId(1), "Fractions"), true);
        var state = LearnerSkillState.Evaluate([evidence, evidence, evidence]);
        Assert.Equal(1, state.Answers);
        Assert.Equal(SkillMastery.Developing, state.Mastery);
        Assert.Equal("Observed answer", evidence.Answer);
        Assert.Equal("A provisional interpretation", evidence.AssessmentFeedback);
        Assert.Equal("test-model", evidence.AssessmentSource);
        Assert.Equal(DateTimeOffset.UnixEpoch, evidence.ObservedAt);
    }

    [Fact]
    public void Evaluate_MixedOwnersOrSkills_RejectsEvidence()
    {
        Assert.Throws<ArgumentException>(() => LearnerSkillState.Evaluate([
            Answer(Skill.Create(new UserId(1), "Fractions"), true),
            Answer(Skill.Create(new UserId(2), "Fractions"), true)]));
    }

    [Fact]
    public void FromAnswer_UnassessedLesson_RejectsEvidence()
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        lesson.LinkToSkill(Skill.Create(new UserId(1), "Topic"));
        Assert.Throws<InvalidOperationException>(() => LearningEvidence.FromAnswer(lesson, DateTimeOffset.UnixEpoch, "test-model"));
        lesson.Finish();
        Assert.Throws<InvalidOperationException>(() => LearningEvidence.FromAnswer(lesson, DateTimeOffset.UnixEpoch, "test-model"));
    }

    [Fact]
    public void LinkToSkill_ForeignOwnerOrReassignment_RejectsSkill()
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        Assert.Throws<ArgumentException>(() => lesson.LinkToSkill(Skill.Create(new UserId(2), "Topic")));
        var skill = Skill.Create(new UserId(1), "Topic");
        lesson.LinkToSkill(skill);
        Assert.Throws<ArgumentException>(() => lesson.LinkToSkill(skill));
    }

    internal static LearningEvidence Answer(Skill skill, bool correct)
    {
        var lesson = Lesson.Start(skill.UserId, skill.Title, "Theory", "Question");
        lesson.LinkToSkill(skill);
        lesson.Complete("Observed answer", "A provisional interpretation", correct);
        return LearningEvidence.FromAnswer(lesson, DateTimeOffset.UnixEpoch, "test-model");
    }
}
