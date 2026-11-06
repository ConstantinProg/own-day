using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class AdaptiveTeachingTests
{
    private static readonly CurriculumTopic[] _topics = [new("a", "Basics", []), new("b", "Advanced", ["a"]), new("c", "Other", [])];

    [Theory]
    [InlineData("Delete")]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("hint")]
    [InlineData(" Hint ")]
    public void TryValidate_InvalidAction_RejectsProposal(string action)
    {
        Assert.False(AdaptiveTeaching.TryValidate(new(action, "a", "Reason"), _topics[0], _topics,
            new HashSet<string>(), new(SkillMastery.Unknown, 0, 0, 0), out _));
    }

    [Fact]
    public void ApplyAdaptation_TransitivePrerequisites_PreservesPriorityOverIndependentTopic()
    {
        var program = LearningProgram.Create(new UserId(1), "Goal", "Experience", null, "Discipline",
            [new("z", "Root", []), new("c", "Other", []), new("a", "Intermediate", ["z"]), new("b", "Selected", ["a"])], null);

        program.ApplyAdaptation(program.ProposeAdaptation("b", "User selected b."));

        Assert.Equal("z", program.NextTopic(new HashSet<string>())!.Code);
        Assert.Equal("a", program.NextTopic(new HashSet<string> { "z" })!.Code);
        Assert.Equal("b", program.NextTopic(new HashSet<string> { "z", "a" })!.Code);
        Assert.Equal("c", program.NextTopic(new HashSet<string> { "z", "a", "b" })!.Code);
    }

    [Theory]
    [InlineData("Hint")]
    [InlineData("Explain")]
    [InlineData("Simplify")]
    [InlineData("Practice")]
    public void TryValidate_RepeatedDifficulty_AcceptsBoundedCorrection(string action)
    {
        Assert.True(AdaptiveTeaching.TryValidate(new(action, "b", "Two negative assessments suggest another example."), _topics[1], _topics,
            new HashSet<string> { "a" }, new(SkillMastery.NeedsPractice, 2, 0, 2), out _));
    }

    [Theory]
    [InlineData("Advance", "b", false)]
    [InlineData("Delete", "b", false)]
    [InlineData("Simplify", "missing", false)]
    [InlineData("Simplify", "c", false)]
    [InlineData("ReviewPrerequisite", "a", true)]
    [InlineData("ReviewPrerequisite", "c", false)]
    [InlineData("ReviewPrerequisite", "b", false)]
    public void TryValidate_ProposalAgainstEvidenceAndGraph_ValidatesTargetAndAction(string action, string code, bool allowed)
    {
        Assert.Equal(allowed, AdaptiveTeaching.TryValidate(new(action, code, "Reason"), _topics[1], _topics,
            new HashSet<string> { "a" }, new(SkillMastery.Conflicting, 3, 1, 2), out _));
    }

    [Fact]
    public void TryValidate_UnknownPrerequisiteOrInvalidReason_RejectsProposal()
    {
        Assert.False(AdaptiveTeaching.TryValidate(new("Advance", "b", "Reason"), _topics[1], _topics,
            new HashSet<string>(), new(SkillMastery.Unknown, 0, 0, 0), out _));
        foreach (var reason in new[] { "", " ", new string('x', 251) })
            Assert.False(AdaptiveTeaching.TryValidate(new("Practice", "a", reason), _topics[0], _topics,
                new HashSet<string>(), new(SkillMastery.Unknown, 0, 0, 0), out _));
    }

    [Fact]
    public void TryValidate_ReadyNextTopic_AllowsAdvanceWithoutClaimingMastery()
    {
        Assert.True(AdaptiveTeaching.TryValidate(new("Advance", "b", "Prerequisite question answered."), _topics[1], _topics,
            new HashSet<string> { "a" }, new(SkillMastery.Unknown, 0, 0, 0), out _));
    }

    [Fact]
    public void ApplyAdaptation_PriorityChange_PreservesGoalGraphHistoryAndProgressIdentity()
    {
        var program = Create();
        var original = program.CurrentRoute;
        var proposal = program.ProposeAdaptation("c", "User selected c.");
        Assert.Same(original, Assert.Single(program.Routes));
        program.ApplyAdaptation(proposal);
        Assert.Equal("Goal", program.Goal);
        Assert.Equal("Discipline", program.Discipline);
        Assert.Equal(2, program.CurrentRoute.Version);
        Assert.Equal("c", program.NextTopic(new HashSet<string>())!.Code);
        Assert.Equal("User selected c.", program.CurrentRoute.Reason);
        Assert.Equal(new[] { 1, 2 }, program.ProgressVersions(2));
        Assert.Equal("a", program.CurrentRoute.Topics.Single(t => t.Code == "b").Prerequisites[0]);
        program.ApplyAdaptation(program.ProposeAdaptation("a", "Another example."));
        Assert.Equal(new[] { 1, 2, 3 }, program.ProgressVersions(3));
        Assert.Same(original, program.Routes.Single(r => r.Version == 1));
    }

    [Fact]
    public void ApplyAdaptation_DiagnosticRevision_DoesNotTransferEarlierTopicIdentity()
    {
        var program = LearningProgram.Create(new UserId(1), "Goal", "Experience", null, "Discipline", _topics, "Question");
        program.ApplyDiagnostic("Answer", "Discipline", _topics);
        program.ApplyAdaptation(program.ProposeAdaptation("c", "Practice."));
        Assert.Equal(new[] { 2, 3 }, program.ProgressVersions(3));
    }

    [Fact]
    public void ApplyAdaptation_ForeignOrStaleProposal_RejectsWithoutMutation()
    {
        var program = Create();
        var proposal = program.ProposeAdaptation("a", "Reason");
        Assert.Throws<ArgumentException>(() => program.ApplyAdaptation(Create().ProposeAdaptation("a", "Reason")));
        Assert.Throws<ArgumentException>(() => program.ProposeAdaptation("missing", "Reason"));
        Assert.Single(program.Routes);
        program.ApplyAdaptation(proposal);
        Assert.Throws<ArgumentException>(() => program.ApplyAdaptation(proposal));
        Assert.Equal(2, program.Routes.Count);
    }

    private static LearningProgram Create() => LearningProgram.Create(new UserId(1), "Goal", "Experience", null, "Discipline", _topics, null);
}
