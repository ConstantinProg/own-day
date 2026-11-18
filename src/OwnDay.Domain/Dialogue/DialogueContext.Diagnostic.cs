using OwnDay.Domain.Learning;

namespace OwnDay.Domain.Dialogue;

public sealed record DiagnosticPromptTarget(Guid ProgramId, string ContentVersion, int ItemIndex, EnglishDiagnosticStatus Status);

public sealed partial class DialogueContext
{
    public void BindDiagnosticQuestion(Guid programId, string question, DateTimeOffset now, long sourceOrder)
    {
        if (programId == Guid.Empty || string.IsNullOrWhiteSpace(question) || TargetLessonId is not null)
            throw new InvalidOperationException("Cannot bind this diagnostic question.");
        ClearPending(now);
        DiagnosticTarget = DiagnosticQuestionTarget(programId, question);
        Phase = DialoguePhase.Diagnostic;
        ExpectedResponse = DialogueExpectedResponse.DiagnosticAnswer;
        PromptId = Guid.NewGuid();
        PromptOpenedAt = now;
        ResponseAfterSourceOrder = Math.Max(ResponseAfterSourceOrder, sourceOrder);
    }

    public bool MatchesDiagnosticQuestion(Guid programId, string question) =>
        ExpectedResponse == DialogueExpectedResponse.DiagnosticAnswer &&
        DiagnosticTarget == DiagnosticQuestionTarget(programId, question);

    private static DiagnosticPromptTarget DiagnosticQuestionTarget(Guid programId, string question) =>
        new(programId, "single-question/" + Hash(question), 0, EnglishDiagnosticStatus.Started);

    public void BindDiagnostic(LearningProgram program, DateTimeOffset now, long sourceOrder)
    {
        if (program.UserId != UserId || !program.DiagnosticPending || program.EnglishDiagnostic is not { Supported: true } diagnostic || TargetLessonId is not null)
            throw new InvalidOperationException("Cannot bind this diagnostic.");
        ClearPending(now);
        DiagnosticTarget = new(program.Id, diagnostic.ContentVersion, diagnostic.Results.Length, diagnostic.Status);
        Phase = diagnostic.Status == EnglishDiagnosticStatus.Offered ? DialoguePhase.DiagnosticOffer : DialoguePhase.Diagnostic;
        ExpectedResponse = diagnostic.Status == EnglishDiagnosticStatus.Offered
            ? DialogueExpectedResponse.DiagnosticChoice : DialogueExpectedResponse.DiagnosticAnswer;
        PromptId = Guid.NewGuid();
        PromptOpenedAt = now;
        ResponseAfterSourceOrder = Math.Max(ResponseAfterSourceOrder, sourceOrder);
    }

    public bool MatchesDiagnostic(LearningProgram program) => program.UserId == UserId &&
        program.EnglishDiagnostic is { Supported: true } diagnostic && DiagnosticTarget is { } target &&
        target.ProgramId == program.Id && target.ContentVersion == diagnostic.ContentVersion &&
        target.ItemIndex == diagnostic.Results.Length && target.Status == diagnostic.Status &&
        (diagnostic.Status == EnglishDiagnosticStatus.Offered
            ? Phase == DialoguePhase.DiagnosticOffer && ExpectedResponse == DialogueExpectedResponse.DiagnosticChoice
            : Phase == DialoguePhase.Diagnostic && ExpectedResponse == DialogueExpectedResponse.DiagnosticAnswer);
}
