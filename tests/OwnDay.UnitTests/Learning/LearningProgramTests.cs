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
        program.ApplyDiagnostic("Observed answer", "Discipline", [new("b", "Different starting point", [])]);
        Assert.Equal(2, program.CurrentRoute.Version);
        Assert.Equal(2, program.Routes.Count);
        Assert.Same(original, program.Routes.Single(r => r.Version == 1));
        Assert.Equal("Goal", program.Goal);
        Assert.Equal("Experience", program.Experience);
        Assert.Equal("Observed answer", program.DiagnosticAnswer);
        Assert.False(program.DiagnosticPending);
        Assert.Throws<InvalidOperationException>(() => program.ApplyDiagnostic("Again", "Discipline", [new("c", "C", [])]));
    }

    [Fact]
    public void ApplyDiagnostic_InvalidRoute_PreservesPendingDiagnosticAndVersion()
    {
        var program = Create([new("a", "Basics", [])], "Question?");
        Assert.Throws<ArgumentException>(() => program.ApplyDiagnostic("Answer", "Discipline", [new("b", "B", ["b"])]));
        Assert.True(program.DiagnosticPending);
        Assert.Null(program.DiagnosticAnswer);
        Assert.Equal(1, program.CurrentRoute.Version);
        Assert.Single(program.Routes);
    }

    [Theory]
    [InlineData("Physics")]
    [InlineData("")]
    [InlineData(null)]
    public void ApplyDiagnostic_DifferentDiscipline_PreservesProgramAndPendingRoute(string? discipline)
    {
        var program = Create([new("a", "Basics", [])], "Question?");
        var originalRoute = program.CurrentRoute;

        Assert.Throws<ArgumentException>(() => program.ApplyDiagnostic("Answer", discipline!, [new("b", "New topic", [])]));

        Assert.Equal("Discipline", program.Discipline);
        Assert.True(program.DiagnosticPending);
        Assert.Null(program.DiagnosticAnswer);
        Assert.Same(originalRoute, Assert.Single(program.Routes));
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

    [Fact]
    public void SetTopicSkipped_Prerequisite_UnlocksChoiceWithoutRecordingCredit()
    {
        var program = Create([new("a", "Basics", []), new("b", "Advanced", ["a"])]);
        var lesson = Lesson.Start(program.UserId, "Basics", "Theory", "Original question?");
        lesson.LinkToProgram(program, "a");

        Assert.True(program.SetTopicSkipped(1, "a", true, "User wants to move on"));
        Assert.False(program.SetTopicSkipped(1, "b", true, "Stale version"));
        Assert.False(program.SetTopicSkipped(2, "a", true, "Already skipped"));
        var completed = new HashSet<string>();
        Assert.Equal("b", program.NextTopic(completed)!.Code);
        Assert.True(program.CurrentRoute.PrerequisitesSatisfied(program.CurrentRoute.Topics[0], completed));
        Assert.Empty(completed);
        Assert.Equal(1, lesson.RouteVersion);
        Assert.Equal("Original question?", lesson.Question);

        Assert.True(program.RestoreRoute(2, 1, "Return to original path"));
        Assert.Equal(1, program.CurrentRoute.Path!.RestoredFrom);
        Assert.Equal("a", program.NextTopic(completed)!.Code);
        Assert.True(program.Routes.Single(route => route.Version == 2).IsSkipped("a"));
        Assert.Equal([1, 2, 3], program.ProgressVersions(3));
        Assert.False(program.RestoreRoute(3, 1, "Already restored"));
    }

    [Fact]
    public void ReviseRoute_ReusedIdentityInvalidDependencyAndStaleEdit_PreserveCurrentRoute()
    {
        var program = Create([new("a", "A", []), new("b", "B", ["a"]), new("c", "C", [])]);
        var original = program.CurrentRoute;
        Assert.Throws<ArgumentException>(() => program.ReviseRoute(1, [new("a", "Other skill", []), new("b", "B", ["a"]), new("c", "C", [])], "Reused ID"));
        Assert.Throws<ArgumentException>(() => program.ReviseRoute(1, [new("a", "A", ["b"]), new("b", "B", ["a"]), new("c", "C", [])], "Cycle"));
        Assert.False(program.ReviseRoute(1, [original.Topics[1], original.Topics[0], original.Topics[2]], "Move prerequisite later"));
        Assert.Same(original, program.CurrentRoute);

        Assert.True(program.ReviseRoute(1, [original.Topics[2], original.Topics[0], original.Topics[1]], "Examples first"));
        Assert.False(program.SetTopicDepth(1, "c", CurriculumDepth.Detailed, "Stale edit"));
        Assert.True(program.SetTopicDepth(2, "c", CurriculumDepth.Detailed, "More context"));
        Assert.Equal(CurriculumDepth.Detailed, program.CurrentRoute.DepthFor("c"));
        Assert.True(program.RestoreRoute(3, 1, "Return to original path"));
        Assert.Equal(CurriculumDepth.Standard, program.CurrentRoute.DepthFor("c"));
        Assert.Equal(["a", "b", "c"], program.CurrentRoute.Topics.Select(topic => topic.Code));
    }

    [Fact]
    public void RestoreRoute_DifferentProgressRoot_IsolatesUnrelatedTopicsWithTheSameCode()
    {
        var program = Create([new("a", "Original skill", [])], "What do you know?");
        program.ApplyDiagnostic("Answer", "Discipline", [new("a", "Different skill", [])]);

        Assert.True(program.RestoreRoute(2, 1, "Restore initial topics"));

        Assert.Equal("Original skill", program.CurrentRoute.Topics[0].Title);
        Assert.Equal([1, 3], program.ProgressVersions(3));
    }

    private static LearningProgram Create(CurriculumTopic[] topics, string? question = null) =>
        LearningProgram.Create(new UserId(1), "Goal", "Experience", null, "Discipline", topics, question);
}
