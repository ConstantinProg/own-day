using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace OwnDay.Domain.Learning;

public enum ChoiceFormat { SingleChoice, MultipleChoice, TrueFalse }
public enum ChoiceOutcome { Incorrect, Partial, Correct }

public sealed record ChoiceOption(int Id, string Text);

public sealed record ChoiceExercise(int Version, ChoiceFormat Format, string Question, ImmutableArray<ChoiceOption> Options,
    ImmutableArray<int> CorrectOptions, string Explanation, ImmutableArray<string> Hints)
{
    public const int CurrentVersion = 1;
    public const int MaxOptions = 6;

    public void Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(Format) || !ValidText(Question, 1000) ||
            !ValidText(Explanation, 1500) || Options.IsDefault || Options.Length is < 2 or > MaxOptions ||
            Options.Any(option => option is null || option.Id is < 1 or > MaxOptions ||
                !ValidText(option.Text, 160) || option.Text.Contains('\n') || option.Text.Contains('\r')) ||
            Options.Select(option => option.Id).Distinct().Count() != Options.Length ||
            Options.Select(option => Normalize(option.Text).TrimEnd('.', '!', '?')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Options.Length ||
            CorrectOptions.IsDefaultOrEmpty || CorrectOptions.Distinct().Count() != CorrectOptions.Length ||
            CorrectOptions.Any(id => !Options.Any(option => option.Id == id)) ||
            (Format == ChoiceFormat.MultipleChoice
                ? Options.Length < 3 || CorrectOptions.Length < 2 || CorrectOptions.Length >= Options.Length
                : CorrectOptions.Length != 1) ||
            (Format == ChoiceFormat.TrueFalse && !Options.Select(option => option.Text).SequenceEqual(["True", "False"])) ||
            Hints.IsDefault || Hints.Length > 1 || Hints.Any(hint => !ValidText(hint, 400)))
            throw new ArgumentException("Invalid published choice exercise.");
    }

    public bool IsValidSelection(ImmutableArray<int> selection) =>
        !selection.IsDefaultOrEmpty && (Format == ChoiceFormat.MultipleChoice || selection.Length == 1) &&
        selection.Distinct().Count() == selection.Length && selection.All(id => Options.Any(option => option.Id == id));

    public ChoiceOutcome Grade(ImmutableArray<int> selection)
    {
        if (!IsValidSelection(selection)) throw new ArgumentException("Invalid option selection.", nameof(selection));
        if (selection.Length == CorrectOptions.Length && selection.All(CorrectOptions.Contains)) return ChoiceOutcome.Correct;
        return Format == ChoiceFormat.MultipleChoice && selection.Length < Options.Length && selection.Any(CorrectOptions.Contains)
            ? ChoiceOutcome.Partial : ChoiceOutcome.Incorrect;
    }

    private static bool ValidText(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max;
    private static string Normalize(string text) => Regex.Replace(text.Normalize().Trim(), @"\s+", " ");
}

public sealed record ChoiceAttempt(Guid? OperationId, ImmutableArray<int> Selected, ChoiceOutcome Outcome,
    int HintsUsed, AnswerAssistance Assistance, bool SolutionSeen, bool Reused);

public sealed record ChoiceExerciseState(ChoiceExercise Exercise, int Revision, ImmutableArray<int> Draft,
    ImmutableArray<ChoiceAttempt> Attempts, int HintsUsed, bool SolutionSeen, bool Reused)
{
    public const int MaxAttempts = 2;
}

public sealed record ChoiceEvidence(int Version, ChoiceFormat Format, string QuestionFingerprint,
    ChoiceOutcome FirstOutcome, ChoiceOutcome FinalOutcome, int Attempts, bool Reused, AnswerAssistance FirstAssistance)
{
    public ExerciseEvidenceKind Kind => ExerciseEvidenceKind.Recognition;
}
