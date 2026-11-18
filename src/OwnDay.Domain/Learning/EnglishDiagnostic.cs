using System.Collections.Immutable;
using DiagnosticState = OwnDay.Domain.Learning.EnglishDiagnostic;

namespace OwnDay.Domain.Learning;

public enum EnglishDiagnosticStatus { Offered, Started, Completed, Skipped }
public sealed record DiagnosticObservation(LearningContent Content, string Answer, string Feedback,
    bool AssessedCorrect, AnswerAssistance Assistance, string AssessmentSource);
public sealed record EnglishDiagnostic(int SchemaVersion, string ContentVersion, EnglishDiagnosticStatus Status,
    ImmutableArray<DiagnosticObservation> Results, AnswerAssistance CurrentAssistance = AnswerAssistance.Independent)
{
    public const int ItemCount = 2;
    public bool Supported => SchemaVersion == 1 && ContentVersion == "work-email-v1";
}

public sealed partial class LearningProgram
{
    public EnglishDiagnostic? EnglishDiagnostic { get; private set; }

    public void OfferEnglishDiagnostic()
    {
        if (EnglishDiagnostic is not null || SubjectDiagnostic is not null || !DiagnosticPending ||
            DiagnosticAnswer is not null || CurrentRoute.Version != 1)
            throw new InvalidOperationException("Only a new pending diagnostic can use the English content.");
        EnglishDiagnostic = new(1, "work-email-v1", EnglishDiagnosticStatus.Offered, []);
    }

    public void StartEnglishDiagnostic()
    {
        RequireEnglishDiagnostic(EnglishDiagnosticStatus.Offered);
        EnglishDiagnostic = EnglishDiagnostic! with { Status = EnglishDiagnosticStatus.Started };
    }

    public void MarkDiagnosticHelp()
    {
        RequireEnglishDiagnostic(EnglishDiagnosticStatus.Started);
        EnglishDiagnostic = EnglishDiagnostic! with { CurrentAssistance = AnswerAssistance.Assisted };
    }

    public void RecordDiagnosticAnswer(LearningContent content, string answer, string feedback, bool correct, string source)
    {
        RequireEnglishDiagnostic(EnglishDiagnosticStatus.Started);
        content.Validate();
        Validate(answer, MaxDiagnosticAnswerLength);
        Validate(feedback, 3000);
        Validate(source, 200);
        var diagnostic = EnglishDiagnostic!;
        var expectedId = diagnostic.Results.Length == 0 ? "email-reading" : "email-writing";
        if (diagnostic.Results.Length >= DiagnosticState.ItemCount || content.Id != expectedId ||
            content.Version != diagnostic.ContentVersion || content.Kind != (diagnostic.Results.Length == 0
                ? LearningContentKind.ReadingChoice : LearningContentKind.OpenWriting))
            throw new InvalidOperationException("Answer does not address the current diagnostic item.");
        EnglishDiagnostic = diagnostic with
        {
            Results = diagnostic.Results.Add(new(content, answer, feedback, correct, diagnostic.CurrentAssistance, source)),
            CurrentAssistance = AnswerAssistance.Independent
        };
    }

    public void CompleteEnglishDiagnostic(IEnumerable<CurriculumTopic> topics)
    {
        RequireEnglishDiagnostic(EnglishDiagnosticStatus.Started);
        if (EnglishDiagnostic!.Results.Length != DiagnosticState.ItemCount)
            throw new InvalidOperationException("Complete each diagnostic item first.");
        var route = CurriculumRoute.Create(Id, CurrentRoute.Version + 1,
            "Route after two provisional English diagnostic observations; no certified level.", topics);
        _routes.Add(route);
        DiagnosticAnswer = string.Join("\n", EnglishDiagnostic.Results.Select(result =>
            result.Content.Id + ": " + result.AssessedCorrect + "; assistance: " + result.Assistance));
        DiagnosticPending = false;
        EnglishDiagnostic = EnglishDiagnostic with { Status = EnglishDiagnosticStatus.Completed };
    }

    private void RequireEnglishDiagnostic(EnglishDiagnosticStatus status)
    {
        if (!DiagnosticPending || EnglishDiagnostic is not { Supported: true } diagnostic || diagnostic.Status != status)
            throw new InvalidOperationException("English diagnostic is not in the expected state.");
    }
}
