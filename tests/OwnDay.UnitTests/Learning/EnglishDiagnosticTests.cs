using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class EnglishDiagnosticTests
{
    [Fact]
    public void RecordAnswer_TwoItems_RequiresCurrentItemAndCreatesOneFinalRoute()
    {
        var program = Program();
        program.StartEnglishDiagnostic();
        Assert.Throws<InvalidOperationException>(() => program.RecordDiagnosticAnswer(EnglishLearningContent.Writing, "Hi", "Feedback", true, "test"));
        program.RecordDiagnosticAnswer(EnglishLearningContent.Reading, "B", "Reading feedback", true, "authored key");
        Assert.Single(program.Routes);
        Assert.Throws<InvalidOperationException>(() => program.CompleteEnglishDiagnostic(EnglishLearningContent.Topics()));
        program.MarkDiagnosticHelp();
        program.RecordDiagnosticAnswer(EnglishLearningContent.Writing, "Yes, I can send it today.", "Writing feedback", true, "test-model");
        Assert.Equal(AnswerAssistance.Assisted, program.EnglishDiagnostic!.Results[1].Assistance);
        Assert.Throws<InvalidOperationException>(() => program.RecordDiagnosticAnswer(EnglishLearningContent.Writing, "Duplicate", "Feedback", true, "test"));
        program.CompleteEnglishDiagnostic(EnglishLearningContent.Topics());
        Assert.Equal(2, program.CurrentRoute.Version);
        Assert.False(program.DiagnosticPending);
        Assert.Equal(EnglishDiagnosticStatus.Completed, program.EnglishDiagnostic.Status);
        Assert.Throws<InvalidOperationException>(() => program.StartEnglishDiagnostic());
        Assert.Throws<InvalidOperationException>(() => program.CompleteEnglishDiagnostic(EnglishLearningContent.Topics()));
    }

    [Fact]
    public void Skip_AfterReading_PreservesObservationWithoutNegativeEvidenceOrRouteRevision()
    {
        var program = Program();
        program.StartEnglishDiagnostic();
        program.RecordDiagnosticAnswer(EnglishLearningContent.Reading, "B", "Feedback", true, "authored key");
        program.SkipDiagnostic();
        Assert.Single(program.EnglishDiagnostic!.Results);
        Assert.Equal(EnglishDiagnosticStatus.Skipped, program.EnglishDiagnostic.Status);
        Assert.Null(program.DiagnosticAnswer);
        Assert.Single(program.Routes);
        Assert.Throws<InvalidOperationException>(program.SkipDiagnostic);
    }

    [Fact]
    public void SetContent_OpenWriting_RejectsExactAnswerKeyAndPreservesRubricInEvidence()
    {
        var user = new UserId(1);
        var invalid = Lesson.Start(user, "English", "Text", "Question", expectedAnswer: "one possible reply");
        Assert.Throws<InvalidOperationException>(() => invalid.SetContent(EnglishLearningContent.FirstLesson));
        var lesson = Lesson.Start(user, "English", "Text", "Question");
        lesson.SetContent(EnglishLearningContent.FirstLesson);
        lesson.LinkToSkill(Skill.Create(user, "Writing"));
        lesson.Complete("I cannot send it today. Tomorrow is OK.", "Meaning, clarity and appropriateness assessed", true);
        var evidence = LearningEvidence.FromAnswer(lesson, DateTimeOffset.UnixEpoch, "test-model");
        Assert.Equal(AnswerAssessmentStatus.AiOnly, evidence.AssessmentStatus);
        Assert.Null(evidence.DeterministicIsCorrect);
        Assert.Equal(EnglishLearningContent.FirstLesson, evidence.Content);
        Assert.Equal(AnswerAssistance.Independent, evidence.Assistance);
    }

    private static LearningProgram Program()
    {
        var program = LearningProgram.Create(new(1), "Хочу читать английские письма", "Beginner", null, "English",
            EnglishLearningContent.Topics(), EnglishLearningContent.ReadingQuestion);
        program.OfferEnglishDiagnostic();
        return program;
    }
}
