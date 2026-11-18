using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class SubjectDiagnosticTests
{
    private static readonly CurriculumTopic[] _topics = [new("root", "Types", [])];

    [Fact]
    public void RecordSubjectObservation_RepeatedQuestionOrPrematureLevel_LeavesQuestionAndEvidenceUntouched()
    {
        var program = Program();
        Assert.Throws<ArgumentException>(() => Record(program, "QUESTION 1", DiagnosticStartingLevel.Unknown));
        Assert.Throws<ArgumentException>(() => Record(program, null, DiagnosticStartingLevel.Independent));
        Assert.Empty(program.SubjectDiagnostic!.Observations);
        Assert.Equal("Question 1", program.DiagnosticQuestion);
        Assert.True(program.DiagnosticPending);
        Assert.Equal(1, program.CurrentRoute.Version);
    }

    [Fact]
    public void RecordSubjectObservation_AtQuestionLimit_RequiresCompletionAndKeepsAllObservations()
    {
        var program = Program();
        for (var index = 2; index <= SubjectDiagnostic.MaxQuestions; index++)
            Record(program, $"Question {index}", DiagnosticStartingLevel.Unknown);

        Assert.Throws<ArgumentException>(() => Record(program, "Question 6", DiagnosticStartingLevel.Unknown));
        Assert.Equal(4, program.SubjectDiagnostic!.Observations.Length);
        Record(program, null, DiagnosticStartingLevel.Foundation);

        Assert.False(program.DiagnosticPending);
        Assert.Equal(5, program.SubjectDiagnostic!.Observations.Length);
        Assert.Equal(DiagnosticStartingLevel.Foundation, program.SubjectDiagnostic.StartingLevel);
        Assert.Equal(2, program.CurrentRoute.Version);
    }

    [Fact]
    public void ResumeSubjectDiagnostic_AfterFirstObservation_PreservesQuestionAndExistingRoute()
    {
        var program = Program();
        Record(program, "Question 2", DiagnosticStartingLevel.Unknown);
        program.SkipDiagnostic();
        Assert.False(program.DiagnosticPending);
        Assert.Equal(DiagnosticStartingLevel.Unknown, program.SubjectDiagnostic!.StartingLevel);
        Assert.Equal("Question 1", Assert.Single(program.SubjectDiagnostic.Observations).Question);

        program.ResumeSubjectDiagnostic();
        Assert.Equal("Question 2", program.DiagnosticQuestion);
        Record(program, null, DiagnosticStartingLevel.Developing);
        Assert.Equal(2, program.SubjectDiagnostic!.Observations.Length);
        Assert.Equal(1, program.CurrentRoute.Version);
    }

    private static LearningProgram Program()
    {
        var program = LearningProgram.Create(new UserId(1), "Use type constraints", "Beginner", null, "TypeScript", _topics, "Question 1");
        program.OfferSubjectDiagnostic();
        return program;
    }

    private static void Record(LearningProgram program, string? nextQuestion, DiagnosticStartingLevel level) =>
        program.RecordSubjectObservation("Answer", "Provisional observation", program.Discipline, _topics, nextQuestion, level);
}
