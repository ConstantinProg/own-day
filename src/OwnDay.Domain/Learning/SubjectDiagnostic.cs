using System.Collections.Immutable;
using System.Text.Json.Serialization;
using DiagnosticState = OwnDay.Domain.Learning.SubjectDiagnostic;

namespace OwnDay.Domain.Learning;

[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticStartingLevel>))]
public enum DiagnosticStartingLevel { Unknown, Foundation, Developing, Independent }

public sealed record SubjectDiagnosticObservation(string Question, string Answer, string Interpretation);

public sealed record SubjectDiagnostic(int Version, ImmutableArray<SubjectDiagnosticObservation> Observations,
    DiagnosticStartingLevel StartingLevel, bool Deferred = false, bool WasDeferred = false)
{
    public const int MaxQuestions = 5;
    public const int MaxInterpretationLength = 350;
    public bool Supported => Version == 1 && !Observations.IsDefault;
}

public sealed partial class LearningProgram
{
    public SubjectDiagnostic? SubjectDiagnostic { get; private set; }

    public void OfferSubjectDiagnostic()
    {
        if (!DiagnosticPending || DiagnosticQuestion is null || EnglishDiagnostic is not null ||
            SubjectDiagnostic is not null || CurrentRoute.Version != 1)
            throw new InvalidOperationException("Only a newly planned subject question can start diagnosis.");
        SubjectDiagnostic = new(1, [], DiagnosticStartingLevel.Unknown);
    }

    public void ResumeSubjectDiagnostic()
    {
        if (DiagnosticPending || SubjectDiagnostic is not { Supported: true, Deferred: true } diagnostic ||
            DiagnosticQuestion is null)
            throw new InvalidOperationException("There is no deferred diagnosis.");
        SubjectDiagnostic = diagnostic with { Deferred = false };
        DiagnosticPending = true;
    }

    public void RecordSubjectObservation(string answer, string interpretation, string discipline,
        IEnumerable<CurriculumTopic> topics, string? nextQuestion, DiagnosticStartingLevel startingLevel)
    {
        if (!DiagnosticPending || DiagnosticQuestion is not { } current ||
            SubjectDiagnostic is not { Supported: true, Deferred: false } diagnostic)
            throw new InvalidOperationException("No current subject question.");
        Validate(answer, MaxDiagnosticAnswerLength);
        Validate(interpretation, DiagnosticState.MaxInterpretationLength);
        var count = diagnostic.Observations.Length + 1;
        if (!string.Equals(Discipline, discipline, StringComparison.Ordinal) ||
            !Enum.IsDefined(startingLevel) || count > DiagnosticState.MaxQuestions ||
            startingLevel != DiagnosticStartingLevel.Unknown && (count < 2 || nextQuestion is not null))
            throw new ArgumentException("Invalid diagnostic interpretation.");
        if (nextQuestion is not null)
        {
            Validate(nextQuestion, MaxDiagnosticQuestionLength);
            if (count == DiagnosticState.MaxQuestions || SameDiagnosticQuestion(nextQuestion, current) ||
                diagnostic.Observations.Any(item => SameDiagnosticQuestion(nextQuestion, item.Question)))
                throw new ArgumentException("Invalid follow-up question.");
        }

        var route = CurriculumRoute.Create(Id, CurrentRoute.Version + 1,
            "Initial difficulty informed by provisional diagnostic observations.", topics);
        var observations = diagnostic.Observations.Add(new(current, answer, interpretation));
        SubjectDiagnostic = diagnostic with { Observations = observations, StartingLevel = startingLevel };
        if (nextQuestion is not null)
        {
            DiagnosticQuestion = nextQuestion;
            return;
        }

        DiagnosticPending = false;
        if (!diagnostic.WasDeferred) _routes.Add(route);
        DiagnosticAnswer = SummarizeSubjectDiagnostic(SubjectDiagnostic);
    }

    private static string SummarizeSubjectDiagnostic(SubjectDiagnostic diagnostic) =>
        $"Provisional starting level: {diagnostic.StartingLevel}.\n" +
        string.Join('\n', diagnostic.Observations.Select(item => item.Interpretation));

    private static bool SameDiagnosticQuestion(string first, string second) =>
        string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
}
