using System.Collections.Immutable;

namespace OwnDay.Domain.Learning;

public sealed partial class Lesson
{
    public ChoiceExerciseState? Choice { get; private set; }

    public void PublishChoice(ChoiceExercise exercise, bool reused)
    {
        RequireAwaitingAnswer();
        exercise.Validate();
        if (Choice is not null || Content is not null || ExpectedAnswer is not null || exercise.Question != Question)
            throw new InvalidOperationException("Choice requires its original standalone question.");
        Choice = new(exercise, 1, [], [], 0, false, reused);
        if (reused) Assistance = AnswerAssistance.Assisted;
    }

    public bool ToggleChoice(int version, int revision, int option)
    {
        if (!MatchesChoice(version, revision) || Choice is not { Exercise.Format: ChoiceFormat.MultipleChoice } state ||
            !state.Exercise.Options.Any(item => item.Id == option))
            return false;
        Choice = state with
        {
            Revision = checked(state.Revision + 1),
            Draft = state.Draft.Contains(option) ? state.Draft.Remove(option) : state.Draft.Add(option)
        };
        return true;
    }

    public bool MatchesChoice(int version, int revision) => AcceptsAnswer &&
        version == ChoiceExercise.CurrentVersion && Choice is { SolutionSeen: false } state &&
        state.Exercise.Version == version && state.Revision == revision;

    public void RefreshChoicePrompt()
    {
        if (AcceptsAnswer) InvalidateChoiceButtons();
    }

    public ChoiceAttempt SubmitChoice(ImmutableArray<int> selected, Guid? operationId, string? answer = null)
    {
        RequireAwaitingAnswer();
        var state = Choice ?? throw new InvalidOperationException("A choice exercise is required.");
        if (state.SolutionSeen || state.Attempts.Length >= ChoiceExerciseState.MaxAttempts ||
            state.Attempts.LastOrDefault()?.Outcome == ChoiceOutcome.Correct ||
            operationId is { } id && (id == Guid.Empty || state.Attempts.Any(attempt => attempt.OperationId == id)))
            throw new InvalidOperationException("Choice attempt already applied, exhausted or revealed.");
        if (answer is not null) Validate(answer, 3000);
        var attempt = new ChoiceAttempt(operationId, [.. selected.Order()], state.Exercise.Grade(selected),
            state.HintsUsed, state.Attempts.Length == 0 ? Assistance : AnswerAssistance.Assisted, state.SolutionSeen, state.Reused, answer);
        Choice = state with { Revision = checked(state.Revision + 1), Draft = [], Attempts = state.Attempts.Add(attempt) };
        return attempt;
    }

    public string RevealChoice(string solution)
    {
        RequireAwaitingAnswer();
        var state = Choice ?? throw new InvalidOperationException("A choice exercise is required.");
        if (state.SolutionSeen) return solution;
        AddExplanation(LessonInteractionKind.FullAnswer, "Choice solution", solution);
        Choice = state with { SolutionSeen = true, Revision = checked(state.Revision + 1) };
        return solution;
    }

    public void CompleteChoice(string answer, string feedback)
    {
        RequireAwaitingAnswer();
        if (Choice is not { Attempts.IsEmpty: false } state)
            throw new InvalidOperationException("A choice attempt is required.");
        var attempt = state.Attempts[^1];
        Validate(answer, 3000);
        Validate(feedback, 3000);
        Answer = answer;
        Feedback = feedback;
        Assistance = attempt.Assistance;
        IsCorrect = DeterministicIsCorrect = attempt.Outcome == ChoiceOutcome.Correct;
        AssessmentStatus = AnswerAssessmentStatus.Deterministic;
        Status = LessonStatus.Completed;
    }

    private void InvalidateChoiceButtons()
    {
        if (Choice is { } state) Choice = state with { Revision = checked(state.Revision + 1) };
    }
}
