using System.Collections.Immutable;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public sealed record ChoicePrompt(Guid LessonId, int Version, int Revision, ChoiceFormat Format, string Question,
    ImmutableArray<ChoiceOption> Options, ImmutableArray<int> Draft, bool InRussian)
{
    public const string LatinLabels = "ABCDEF";
    public const string CyrillicLabels = "АБВГДЕ";
    public LearningContinuation? AfterSolution { get; init; }
    public bool AfterAttempt { get; init; }

    public static ChoicePrompt? From(Lesson lesson) => lesson.Choice is { } state && lesson.AcceptsAnswer
        ? new(lesson.Id, state.Exercise.Version, state.Revision, state.Exercise.Format, state.Exercise.Question,
            state.Exercise.Options, state.Draft, MarketLearningContent.InRussian(lesson.Theory))
        {
            AfterAttempt = !state.Attempts.IsEmpty,
            AfterSolution = state.SolutionSeen
                ? lesson.ProgramId is not null ? LearningContinuation.NextTopic : LearningContinuation.Practice
                : null
        }
        : null;
}

public enum ChoiceAction { Select, Toggle, Submit }
public sealed record ChoiceInput(Guid LessonId, int Version, int Revision, ChoiceAction Action, int Option = 0)
{
    internal bool Matches(Lesson? lesson) => lesson?.Id == LessonId && lesson.MatchesChoice(Version, Revision);
}

public sealed record ChoiceApplication(LearningResult Reply, bool AttemptRecorded = false);

internal static class ChoiceTeaching
{
    public static ChoiceFormat? Prefer(LessonRequest request, IReadOnlyList<LearningEvidence>? history = null)
    {
        if (request is { IsReview: true } or { ProjectPractice: not null } or { Transfer: not null } or
            { SharedSkill.NeedsVerification: true } or { RouteDepth: CurriculumDepth.Detailed } or
            { PracticeEvidenceKind: ExerciseEvidenceKind.Application or ExerciseEvidenceKind.Understanding } ||
            AdaptiveTeaching.NeedsOpenCheck(history?.LastOrDefault()))
            return null;

        var state = request.Teaching?.State ?? request.SharedSkill?.State;
        if (state?.RepeatedDifficulty == true || request.Teaching?.Action is TeachingAction.Simplify or TeachingAction.Hint)
            return ChoiceFormat.TrueFalse;
        if (state?.Mastery is SkillMastery.Developing or SkillMastery.Demonstrated ||
            history?.Any(item => item.EffectiveCorrect && item.Assistance == AnswerAssistance.Independent) == true)
            return ChoiceFormat.MultipleChoice;
        return ChoiceFormat.SingleChoice;
    }

    public static TeachingAction AfterAttempt(Lesson lesson, bool needsScaffolding)
    {
        var choice = lesson.Choice!;
        if (!lesson.CanOfferMoreHelp || choice.Reused || lesson.IsReview || choice.Exercise.Format == ChoiceFormat.TrueFalse ||
            choice.Attempts.Length >= ChoiceExerciseState.MaxAttempts ||
            choice.Attempts.Length > 1 && needsScaffolding && choice.Attempts[^2].Outcome == ChoiceOutcome.Incorrect)
            return TeachingAction.Explain;
        return choice.Attempts.Length > 1 || choice.HintsUsed > 0 || needsScaffolding && choice.Attempts[^1].Outcome == ChoiceOutcome.Incorrect
            ? TeachingAction.Simplify : TeachingAction.Hint;
    }
}

internal static class ChoiceAnswerText
{
    public static bool LooksLikeAnswer(ChoiceExercise exercise, string answer)
    {
        var normalized = Normalize(answer);
        if (exercise.Format == ChoiceFormat.TrueFalse && TruthIndex(normalized) >= 0) return true;
        if (FindOption(exercise, answer) is not null) return true;
        var parts = SplitLabels(normalized);
        return parts.Length > 0 && parts.All(part => part.Length == 1 && char.IsLetter(part[0]));
    }

    public static ImmutableArray<int> Parse(ChoiceExercise exercise, string answer)
    {
        var normalized = Normalize(answer);
        if (exercise.Format == ChoiceFormat.TrueFalse)
        {
            var truth = TruthIndex(normalized);
            if (truth >= 0) return [exercise.Options[truth].Id];
        }

        var selected = ImmutableArray.CreateBuilder<int>();
        foreach (var part in SplitLabels(normalized))
        {
            if (part.Length != 1) return [];
            var index = ChoicePrompt.LatinLabels.IndexOf(part[0], StringComparison.Ordinal);
            if (index < 0) index = ChoicePrompt.CyrillicLabels.IndexOf(part[0], StringComparison.Ordinal);
            if (index < 0 || index >= exercise.Options.Length) return [];
            selected.Add(exercise.Options[index].Id);
        }
        return selected.ToImmutable();
    }

    private static string Normalize(string answer) => answer.Trim().ToUpperInvariant().TrimEnd('.', '!');

    public static ChoiceOption? FindOption(ChoiceExercise exercise, string text) => exercise.Options.FirstOrDefault(option =>
        string.Equals(option.Text.TrimEnd('.', '!'), text.Trim().TrimEnd('.', '!'), StringComparison.OrdinalIgnoreCase));

    private static string[] SplitLabels(string answer) => answer.Split([' ', '\t', '\r', '\n', ',', ';', '+', '/'],
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int TruthIndex(string answer) => answer switch
    {
        "TRUE" or "ВЕРНО" or "ИСТИНА" => 0,
        "FALSE" or "НЕВЕРНО" or "ЛОЖЬ" => 1,
        _ => -1
    };
}

public sealed partial class LearningService
{
    internal async Task<ChoiceApplication> ApplyChoiceAsync(UserId user, Lesson? lesson, ChoiceInput input, CancellationToken token)
    {
        ValidateUser(user, token);
        if (lesson is not null && lesson.UserId != user)
            throw new ArgumentException("The lesson belongs to another learner.", nameof(lesson));
        if (lesson is null || !input.Matches(lesson))
        {
            var russian = replyInRussian ?? (lesson is null || MarketLearningContent.InRussian(lesson.Theory));
            return new(new LearningResult.ChoiceReply(russian
                ? "Эта кнопка устарела. Используйте последние варианты или напишите «Продолжим»."
                : "This button is out of date. Use the latest options or write “continue”.",
                lesson is null ? null : ChoicePrompt.From(lesson)));
        }

        var state = lesson.Choice!;
        if (input.Action == ChoiceAction.Toggle && lesson.ToggleChoice(input.Version, input.Revision, input.Option))
            return new(new LearningResult.ChoiceReply("", ChoicePrompt.From(lesson)));

        ImmutableArray<int> selection = input.Action switch
        {
            ChoiceAction.Submit when state.Exercise.Format == ChoiceFormat.MultipleChoice => state.Draft,
            ChoiceAction.Select when state.Exercise.Format != ChoiceFormat.MultipleChoice => [input.Option],
            _ => []
        };
        return state.Exercise.IsValidSelection(selection)
            ? new(await SubmitChoiceAsync(lesson, selection, token), AttemptRecorded: true)
            : new(InvalidChoice(lesson));
    }

    private async Task<Lesson> CreateWithChoiceHistoryAsync(UserId user, string topic, LessonMaterial material, CancellationToken token)
    {
        var reused = material.Choice is not null && await store.ChoiceQuestionSeenAsync(user, material.Question, token);
        return material.CreateLesson(user, topic, reused);
    }

    private async Task<LearningResult> AnswerChoiceAsync(Lesson lesson, string answer, CancellationToken token)
    {
        var choice = lesson.Choice!;
        if (choice.SolutionSeen)
            return new LearningResult.ChoiceReply("", ChoicePrompt.From(lesson));
        var exercise = choice.Exercise;
        var selection = ChoiceAnswerText.Parse(exercise, answer);
        if (exercise.IsValidSelection(selection)) return await SubmitChoiceAsync(lesson, selection, token, answer);

        var option = ChoiceAnswerText.FindOption(exercise, answer);
        if (option is not null) return await SubmitChoiceAsync(lesson, [option.Id], token, answer);
        if (ChoiceAnswerText.LooksLikeAnswer(exercise, answer)) return InvalidChoice(lesson);

        try
        {
            var interpretation = await teacher.InterpretChoiceAsync(new(exercise.Question, exercise.Format, exercise.Options, answer), token);
            token.ThrowIfCancellationRequested();
            selection = interpretation.SelectedOptions ?? [];
        }
        catch (LessonProviderException)
        {
            return InvalidChoice(lesson);
        }
        if (exercise.IsValidSelection(selection))
            return await SubmitChoiceAsync(lesson, selection, token, answer);
        return new LearningResult.ChoiceReply((replyInRussian ?? MarketLearningContent.InRussian(lesson.Theory))
            ? "Не удалось однозначно связать ваш ответ с вариантами. Уточните, какой вариант вы имеете в виду, или выберите его ниже."
            : "I couldn't match your answer to the options with confidence. Clarify which option you mean, or select it below.",
            ChoicePrompt.From(lesson));
    }

    private async Task<LearningResult> SubmitChoiceAsync(Lesson lesson, ImmutableArray<int> selection, CancellationToken token, string? answer = null)
    {
        var russian = replyInRussian ?? MarketLearningContent.InRussian(lesson.Theory);
        var attempt = lesson.SubmitChoice(selection, operationId, answer);
        var verdict = attempt.Outcome switch
        {
            ChoiceOutcome.Correct => russian ? "Верно." : "Correct.",
            ChoiceOutcome.Partial => russian ? "Набор выбран частично верно." : "The selection is partially correct.",
            _ => russian ? "Ответ неверный." : "Incorrect."
        };
        if (attempt.Outcome != ChoiceOutcome.Correct)
        {
            var history = lesson.SkillId is { } skillId ? await knowledge.StatusAsync(lesson.UserId, skillId, token) : null;
            var program = curriculum is not null && (lesson.ProgramId ?? lesson.PracticeProgramId) is { } programId
                ? await curriculum.FindAsync(lesson.UserId, programId, token) : null;
            var needsScaffolding = history?.State.RepeatedDifficulty == true ||
                lesson.Teaching?.Strategy is PedagogicalStrategy.Simplify or PedagogicalStrategy.ReviewPrerequisite;
            var action = ChoiceTeaching.AfterAttempt(lesson, needsScaffolding);
            if (action != TeachingAction.Explain)
            {
                var foundation = program is not null &&
                    LessonProgramContext.From(program).StartingLevel == DiagnosticStartingLevel.Foundation;
                var hint = GiveChoiceScaffold(lesson, action, russian, foundation);
                return new LearningResult.ChoiceReply(verdict + "\n" + hint, ChoicePrompt.From(lesson));
            }
        }

        return await CompleteChoiceAsync(lesson, verdict, token);
    }

    private async Task<LearningResult> CompleteChoiceAsync(Lesson lesson, string verdict, CancellationToken token)
    {
        var russian = replyInRussian ?? MarketLearningContent.InRussian(lesson.Theory);
        var state = lesson.Choice!;
        var attempt = state.Attempts[^1];
        var solution = attempt.Outcome == ChoiceOutcome.Correct ? state.Exercise.Explanation : ChoiceSolution(state.Exercise, russian);
        if (attempt.Outcome != ChoiceOutcome.Correct && !state.SolutionSeen) lesson.RevealChoice(solution);
        var feedback = string.IsNullOrEmpty(verdict) ? solution : verdict + "\n" + solution;
        if (attempt.Assistance == AnswerAssistance.Assisted && attempt.Outcome == ChoiceOutcome.Correct)
            feedback += russian ? "\nОтвет получен с помощью или после повторного предъявления." : "\nAnswer given with help or after seeing this question before.";
        await knowledge.AttachAsync(lesson, lesson.Topic, token);
        lesson.CompleteChoice(attempt.Answer ?? string.Join(", ", state.Exercise.Options.Where(option => attempt.Selected.Contains(option.Id))
            .Select(option => ChoiceLabel(option, state.Exercise.Format, russian))), feedback);
        var evidence = await knowledge.RecordAnswerAsync(lesson, "Published choice key", token);
        var learner = await knowledge.StateWithAnswerAsync(evidence, token);
        var continuation = learner.EvidenceSummary is { Understanding: true, Application: true } && attempt.Outcome == ChoiceOutcome.Correct
            ? LearningContinuation.Review : lesson.ProgramId is not null ? LearningContinuation.NextTopic : LearningContinuation.Practice;
        return new LearningResult.ChoiceReply(feedback, Continuation: continuation)
        {
            AssessedLessonId = lesson.Id, IsCorrect = attempt.Outcome == ChoiceOutcome.Correct
        };
    }

    private static string GiveChoiceScaffold(Lesson lesson, TeachingAction action, bool russian, bool foundation)
    {
        var state = lesson.Choice!;
        var exercise = state.Exercise;
        var attempt = state.Attempts.LastOrDefault();
        var simplified = action == TeachingAction.Simplify;
        var distractor = exercise.Guidance.IsDefaultOrEmpty || attempt is null ? null :
            exercise.Guidance.FirstOrDefault(item => attempt.Selected.Contains(item.OptionId));
        var hint = distractor is null ? null : simplified ? distractor.Example : distractor.Hint;
        if (hint is null || lesson.Interactions.Any(item => item.Explanation.Equals(hint, StringComparison.OrdinalIgnoreCase)))
        {
            hint = !simplified && state.HintsUsed == 0 ? exercise.Hints.FirstOrDefault() : null;
            hint ??= (simplified, foundation, russian) switch
            {
                (true, true, true) => "Представьте один простой случай из объяснения. Опишите обычными словами, что в нём происходит, и сопоставьте это с условием вопроса.",
                (true, true, false) => "Picture one simple case from the lesson. Describe what happens in everyday words, then compare it with the question.",
                (true, _, true) => "Возьмите пример из объяснения и измените в нём одно условие. Проверьте, изменится ли вывод, прежде чем снова выбрать ответ.",
                (true, _, false) => "Change one condition in the lesson example. Check whether its conclusion changes before choosing again.",
                (_, _, true) => "Выделите в вопросе условие, которое должен выполнить ответ. Проверьте выбранный вариант именно по этому условию.",
                _ => "Identify the condition the answer must satisfy. Test the option you considered against that condition."
            };
            if (attempt is { Outcome: ChoiceOutcome.Incorrect, Selected.Length: 1 } && exercise.Format == ChoiceFormat.SingleChoice)
            {
                var selected = exercise.Options.Single(option => option.Id == attempt.Selected[0]).Text;
                hint = (russian ? $"Проверьте вариант «{selected}». " : $"Revisit the choice “{selected}”. ") + hint;
            }
        }
        lesson.AddExplanation(simplified ? LessonInteractionKind.AlternativeExplanation : LessonInteractionKind.Clarification,
            "Choice hint", hint);
        return hint;
    }

    private LearningResult.ChoiceReply InvalidChoice(Lesson lesson)
    {
        var russian = replyInRussian ?? MarketLearningContent.InRussian(lesson.Theory);
        return new(russian ? "Выберите допустимый непустой ответ из вариантов ниже."
            : "Choose a valid, nonempty answer from the options below.", ChoicePrompt.From(lesson));
    }

    private async Task<LearningResult> ExplainChoiceAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token)
    {
        var russian = replyInRussian ?? MarketLearningContent.InRussian(lesson.Theory);
        var choice = lesson.Choice!;
        if (kind == LessonInteractionKind.FullAnswer)
        {
            if (!choice.Attempts.IsEmpty) return await CompleteChoiceAsync(lesson, "", token);
            var solution = lesson.RevealChoice(ChoiceSolution(choice.Exercise, russian));
            return new LearningResult.ChoiceReply(solution, ChoicePrompt.From(lesson));
        }
        if (kind == LessonInteractionKind.Clarification && IsHintRequest(request))
        {
            if (!lesson.CanOfferMoreHelp) return new LearningResult.HelpLimit();
            return new LearningResult.ChoiceReply(GiveChoiceScaffold(lesson,
                choice.HintsUsed == 0 ? TeachingAction.Hint : TeachingAction.Simplify, russian, foundation: false), ChoicePrompt.From(lesson));
        }
        if (!lesson.CanOfferMoreHelp) kind = LessonInteractionKind.ConceptExplanation;
        try
        {
            var snapshot = LessonSnapshot.From(lesson);
            snapshot = kind == LessonInteractionKind.ConceptExplanation
                ? snapshot with { Question = "", Content = null, RubricEvaluation = null }
                : snapshot with { ChoiceResponse = ChoiceResponseContext.From(choice) };
            var explanation = await teacher.ExplainAsync(snapshot, request, kind, token);
            token.ThrowIfCancellationRequested();
            lesson.AddExplanation(kind, request, explanation.Explanation, provenance: explanation.Provenance);
            return new LearningResult.ChoiceReply(explanation.Explanation, ChoicePrompt.From(lesson))
            {
                Provenance = explanation.Provenance
            };
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new LearningResult.ProviderUnavailable();
        }
    }

    private static bool IsHintRequest(string request) => request.TrimEnd('.', '!', '?').ToLowerInvariant() is
        "дай короткую подсказку" or "дай подсказку" or "подскажи" or "подсказка" or "give me a short hint" or "hint" or "could you give another hint";

    private static string ChoiceSolution(ChoiceExercise exercise, bool russian) =>
        (russian ? "Правильный выбор — " : "Correct selection — ") + string.Join(", ", exercise.Options
            .Where(option => exercise.CorrectOptions.Contains(option.Id))
            .Select(option => ChoiceLabel(option, exercise.Format, russian))) + "\n" + exercise.Explanation;

    public static string ChoiceLabel(ChoiceOption option, ChoiceFormat format, bool russian) =>
        format == ChoiceFormat.TrueFalse && russian ? option.Text == "True" ? "Верно" : "Неверно" : option.Text;
}
