using OwnDay.Application.Dialogue;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Dialogue;

public sealed class FirstGoalConsultationTests
{
    private static readonly UserId Owner = new(101);
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void CompleteCreation_ProviderFails_PreservesOriginalDetailsAndRequiresExplicitRetry()
    {
        var context = Draft();
        var result = FirstGoalConsultation.CompleteCreation(context, new CurriculumResult.ProviderUnavailable(), Turn());
        Assert.Equal("Хочу читать письма", context.Consultation!.OriginalGoal);
        Assert.Equal("Рабочая переписка", context.Consultation.PracticalUse);
        Assert.Equal("Понимаю отдельные слова", context.Consultation.Experience);
        Assert.Null(context.Consultation.CreatedProgramId);
        Assert.Equal(DialoguePhase.Consultation, context.Phase);
        Assert.Equal(DialogueReplyKind.ConsultationRetry, result.Reply!.Kind);
        Assert.Null(result.Creation);
    }

    [Fact]
    public void CompleteCreation_MatchingProgram_OffersDiagnosticWithoutStartingLessonOrChangingGoal()
    {
        var context = Draft();
        var program = LearningProgram.Create(Owner, context.Consultation!.OriginalGoal, "Reported context", null,
            "English", [new("root", "Read a request", [])], null);
        var result = FirstGoalConsultation.CompleteCreation(context, new CurriculumResult.Program(program), Turn());
        Assert.Equal(program.Id, context.Consultation.CreatedProgramId);
        Assert.Equal(DialoguePhase.DiagnosticOffer, context.Phase);
        Assert.Equal(DialogueExpectedResponse.DiagnosticChoice, context.ExpectedResponse);
        Assert.Equal(DialogueReplyKind.FirstProgramReady, result.Reply!.Kind);
        Assert.Null(context.TargetLessonId);
        Assert.Equal("Хочу читать письма", program.Goal);
        Assert.Null(result.Execute);
    }

    private static DialogueContext Draft()
    {
        var context = DialogueContext.Create(Owner, null, Now);
        context.BeginConsultation(Guid.NewGuid(), "Хочу читать письма", Now, 1);
        context.RecordPracticalUse("Рабочая переписка", Now, 2);
        context.RecordExperience("Понимаю отдельные слова", Now, 3);
        return context;
    }
    private static DialogueTurn Turn() => new(Guid.NewGuid(), Owner, null, "Понимаю отдельные слова", null, null, Now, Now, SourceOrder: 3);
}
