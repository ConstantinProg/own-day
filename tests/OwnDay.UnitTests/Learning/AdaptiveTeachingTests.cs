using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class AdaptiveTeachingTests
{
    private static readonly CurriculumTopic[] _topics = [new("a", "Basics", []), new("b", "Advanced", ["a"]), new("c", "Other", [])];

    [Theory]
    [InlineData("Hint")]
    [InlineData("Explain")]
    [InlineData("Simplify")]
    [InlineData("Practice")]
    public void IsAllowed_RepeatedDifficulty_AcceptsBoundedCorrection(string action)
    {
        Assert.True(AdaptiveTeaching.IsAllowed(new(action, "b", "Two negative assessments suggest another example."), _topics[1], _topics,
            new HashSet<string> { "a" }, new(SkillMastery.NeedsPractice, 2, 0, 2)));
    }

    [Theory]
    [InlineData("Advance", "b", false)]
    [InlineData("Delete", "b", false)]
    [InlineData("Simplify", "missing", false)]
    [InlineData("Simplify", "c", false)]
    [InlineData("ReviewPrerequisite", "a", true)]
    [InlineData("ReviewPrerequisite", "c", false)]
    [InlineData("ReviewPrerequisite", "b", false)]
    public void IsAllowed_ProposalAgainstEvidenceAndGraph_ValidatesTargetAndAction(string action, string code, bool allowed)
    {
        Assert.Equal(allowed, AdaptiveTeaching.IsAllowed(new(action, code, "Reason"), _topics[1], _topics,
            new HashSet<string> { "a" }, new(SkillMastery.Conflicting, 3, 1, 2)));
    }

    [Fact]
    public void IsAllowed_UnknownPrerequisiteOrInvalidReason_RejectsProposal()
    {
        Assert.False(AdaptiveTeaching.IsAllowed(new("Advance", "b", "Reason"), _topics[1], _topics,
            new HashSet<string>(), new(SkillMastery.Unknown, 0, 0, 0)));
        foreach (var reason in new[] { "", " ", new string('x', 251) })
            Assert.False(AdaptiveTeaching.IsAllowed(new("Practice", "a", reason), _topics[0], _topics,
                new HashSet<string>(), new(SkillMastery.Unknown, 0, 0, 0)));
    }

    [Fact]
    public void IsAllowed_ReadyNextTopic_AllowsAdvanceWithoutClaimingMastery()
    {
        Assert.True(AdaptiveTeaching.IsAllowed(new("Advance", "b", "Prerequisite question answered."), _topics[1], _topics,
            new HashSet<string> { "a" }, new(SkillMastery.Unknown, 0, 0, 0)));
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
