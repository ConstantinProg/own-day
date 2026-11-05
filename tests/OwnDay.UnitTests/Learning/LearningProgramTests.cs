using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LearningProgramTests
{
    [Fact]
    public void NextTopic_UnorderedDependencies_SelectsPrerequisiteBeforeDependent()
    {
        var program = Create([new("b", "Advanced", ["a"]), new("a", "Basics", [])]);
        Assert.Equal("a", program.NextTopic(new HashSet<string>())!.Code);
        Assert.Equal("b", program.NextTopic(new HashSet<string> { "a" })!.Code);
        Assert.Null(program.NextTopic(new HashSet<string> { "a", "b" }));
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("self")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("duplicate prerequisite")]
    [InlineData("invalid code")]
    [InlineData("empty")]
    [InlineData("too many")]
    public void Create_InvalidRoute_RejectsProposal(string kind)
    {
        CurriculumTopic[] topics = kind switch
        {
            "cycle" => [new("a", "A", ["b"]), new("b", "B", ["a"])],
            "self" => [new("a", "A", ["a"])],
            "missing" => [new("a", "A", ["missing"])],
            "duplicate" => [new("a", "A", []), new("a", "B", [])],
            "duplicate prerequisite" => [new("a", "A", []), new("b", "B", ["a", "a"])],
            "invalid code" => [new("a b", "A", [])],
            "too many" => Enumerable.Range(0, 13).Select(i => new CurriculumTopic($"t{i}", "Topic", [])).ToArray(),
            _ => []
        };
        Assert.Throws<ArgumentException>(() => Create(topics));
    }

    [Fact]
    public void ApplyDiagnostic_ValidAnswer_PreservesGoalAndHistoricalRoute()
    {
        var program = Create([new("a", "Basics", [])], "Question?");
        Assert.Throws<InvalidOperationException>(() => program.NextTopic(new HashSet<string>()));
        var original = program.CurrentRoute;
        program.ApplyDiagnostic("Observed answer", [new("b", "Different starting point", [])]);
        Assert.Equal(2, program.CurrentRoute.Version);
        Assert.Equal(2, program.Routes.Count);
        Assert.Same(original, program.Routes.Single(r => r.Version == 1));
        Assert.Equal("Goal", program.Goal);
        Assert.Equal("Experience", program.Experience);
        Assert.Equal("Observed answer", program.DiagnosticAnswer);
        Assert.False(program.DiagnosticPending);
        Assert.Throws<InvalidOperationException>(() => program.ApplyDiagnostic("Again", [new("c", "C", [])]));
    }

    [Fact]
    public void ApplyDiagnostic_InvalidRoute_PreservesPendingDiagnosticAndVersion()
    {
        var program = Create([new("a", "Basics", [])], "Question?");
        Assert.Throws<ArgumentException>(() => program.ApplyDiagnostic("Answer", [new("b", "B", ["b"])]));
        Assert.True(program.DiagnosticPending);
        Assert.Null(program.DiagnosticAnswer);
        Assert.Equal(1, program.CurrentRoute.Version);
        Assert.Single(program.Routes);
    }

    [Fact]
    public void SkipDiagnostic_OptionalQuestion_AllowsLessonWithoutCreatingAssessment()
    {
        var program = Create([new("a", "Basics", [])], "Question?");
        program.SkipDiagnostic();
        Assert.False(program.DiagnosticPending);
        Assert.Null(program.DiagnosticAnswer);
        Assert.Equal(1, program.CurrentRoute.Version);
        Assert.NotNull(program.NextTopic(new HashSet<string>()));
    }

    [Fact]
    public void Create_MutableProposalInput_CopiesDependenciesAndProtectsHistory()
    {
        string[] prerequisites = ["a"];
        var topic = new CurriculumTopic("b", "B", prerequisites);
        prerequisites[0] = "missing";
        var program = Create([topic, new("a", "A", [])]);
        Assert.Equal("a", program.CurrentRoute.Topics[1].Prerequisites[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)topic.Prerequisites)[0] = "missing");
    }

    private static LearningProgram Create(CurriculumTopic[] topics, string? question = null) =>
        LearningProgram.Create(new UserId(1), "Goal", "Experience", null, "Discipline", topics, question);
}
