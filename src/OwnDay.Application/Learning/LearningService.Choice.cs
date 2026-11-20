using System.Collections.Immutable;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public sealed record ChoicePrompt(Guid LessonId, int Version, int Revision, ChoiceFormat Format, string Question,
    ImmutableArray<ChoiceOption> Options, ImmutableArray<int> Draft, bool InRussian)
{
    public const string LatinLabels = "ABCDEF";
    public const string CyrillicLabels = "АБВГДЕ";

    public static ChoicePrompt? From(Lesson lesson) => lesson.Choice is { } state && lesson.Status == LessonStatus.AwaitingAnswer
        ? new(lesson.Id, state.Exercise.Version, state.Revision, state.Exercise.Format, state.Exercise.Question,
            state.Exercise.Options, state.Draft, MarketLearningContent.InRussian(lesson.Theory))
        : null;
}

public enum ChoiceAction { Select, Toggle, Submit }
public sealed record ChoiceInput(Guid LessonId, int Version, int Revision, ChoiceAction Action, int Option = 0);

internal static class ChoiceTeaching
{
    public static bool IsFreshOpenQuestion(LessonMaterial material, IEnumerable<string> previousQuestions)
    {
        if (material.Choice is not null || string.IsNullOrWhiteSpace(material.Question)) return false;
        var fingerprint = RubricEvidence.FingerprintQuestion(material.Question);
        return previousQuestions.All(question => RubricEvidence.FingerprintQuestion(question) != fingerprint);
    }

    public static ChoiceFormat? Prefer(LessonRequest request, IReadOnlyList<LearningEvidence>? history = null)
    {
        if (request is { IsReview: true } or { ProjectPractice: not null } or { Transfer: not null } or
            { SharedSkill.NeedsVerification: true } or { RouteDepth: CurriculumDepth.Detailed } or
            { PracticeEvidenceKind: ExerciseEvidenceKind.Application or ExerciseEvidenceKind.Understanding } ||
            history?.LastOrDefault()?.ChoiceEvidence is not null)
            return null;

        var state = request.Teaching?.State ?? request.SharedSkill?.State;
        if (state?.RepeatedDifficulty == true || request.Teaching?.Action is TeachingAction.Simplify or TeachingAction.Hint)
            return ChoiceFormat.TrueFalse;
        if (state?.Mastery is SkillMastery.Developing or SkillMastery.Demonstrated ||
            history?.Any(item => item.EffectiveCorrect && item.Assistance == AnswerAssistance.Independent) == true)
            return ChoiceFormat.MultipleChoice;
        return ChoiceFormat.SingleChoice;
    }
}

internal static class ChoiceAnswerText
{
    public static ImmutableArray<int> Parse(ChoiceExercise exercise, string answer)
    {
        answer = answer.Trim();
        if (exercise.Format == ChoiceFormat.TrueFalse)
        {
            var truth = answer.ToUpperInvariant().TrimEnd('.', '!') switch
            {
                "TRUE" or "ВЕРНО" or "ИСТИНА" => 0,
                "FALSE" or "НЕВЕРНО" or "ЛОЖЬ" => 1,
                _ => -1
            };
            if (truth >= 0) return [exercise.Options[truth].Id];
        }

        var selected = ImmutableArray.CreateBuilder<int>();
        foreach (var part in answer.ToUpperInvariant().TrimEnd('.').Split([' ', '\t', '\r', '\n', ',', ';', '+', '/'],
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length != 1) return [];
            var index = ChoicePrompt.LatinLabels.IndexOf(part[0], StringComparison.Ordinal);
            if (index < 0) index = ChoicePrompt.CyrillicLabels.IndexOf(part[0], StringComparison.Ordinal);
            if (index < 0 || index >= exercise.Options.Length) return [];
            selected.Add(exercise.Options[index].Id);
        }
        return selected.ToImmutable();
    }
}

public sealed partial class LearningService
{
    public static bool IsChoiceText(Lesson lesson, string text) =>
        lesson.Choice is { } choice && choice.Exercise.IsValidSelection(ChoiceAnswerText.Parse(choice.Exercise, text));

    public async Task<LearningResult> ApplyChoiceAsync(UserId user, ChoiceInput input, CancellationToken token)
    {
        ValidateUser(user, token);
        var lesson = await store.FindActiveAsync(user, token);
        if (lesson?.Id != input.LessonId || !lesson.MatchesChoice(input.Version, input.Revision))
        {
            var russian = lesson is null || MarketLearningContent.InRussian(lesson.Theory);
            return new LearningResult.ChoiceReply(russian
                ? "Эта кнопка устарела. Используйте последние варианты или напишите «Продолжим»."
                : "This button is out of date. Use the latest options or write “continue”.",
                lesson is null ? null : ChoicePrompt.From(lesson));
        }

        var state = lesson.Choice!;
        if (input.Action == ChoiceAction.Toggle && lesson.ToggleChoice(input.Version, input.Revision, input.Option))
            return new LearningResult.ChoiceReply("", ChoicePrompt.From(lesson));

        ImmutableArray<int> selection = input.Action switch
        {
            ChoiceAction.Submit when state.Exercise.Format == ChoiceFormat.MultipleChoice => state.Draft,
            ChoiceAction.Select when state.Exercise.Format != ChoiceFormat.MultipleChoice => [input.Option],
            _ => []
        };
        return state.Exercise.IsValidSelection(selection)
            ? await SubmitChoiceAsync(lesson, selection, token)
            : InvalidChoice(lesson);
    }

    private async Task<Lesson> CreateWithChoiceHistoryAsync(UserId user, string topic, LessonMaterial material, CancellationToken token)
    {
        var reused = material.Choice is not null && await store.ChoiceQuestionSeenAsync(user, material.Question, token);
        return material.CreateLesson(user, topic, reused);
    }

    private async Task<LearningResult> SubmitChoiceAsync(Lesson lesson, ImmutableArray<int> selection, CancellationToken token)
    {
        var russian = MarketLearningContent.InRussian(lesson.Theory);
        var attempt = lesson.SubmitChoice(selection, operationId);
        var state = lesson.Choice!;
        var verdict = attempt.Outcome switch
        {
            ChoiceOutcome.Correct => russian ? "Верно." : "Correct.",
            ChoiceOutcome.Partial => russian ? "Набор выбран частично верно." : "The selection is partially correct.",
            _ => russian ? "Ответ неверный." : "Incorrect."
        };
        if (attempt.Outcome != ChoiceOutcome.Correct && state.Attempts.Length < ChoiceExerciseState.MaxAttempts &&
            !state.SolutionSeen && !state.Reused && !lesson.IsReview)
        {
            var hint = lesson.GiveChoiceHint();
            var retry = russian ? "Попробуйте ещё раз." : "Try once more.";
            return new LearningResult.ChoiceReply(string.Join('\n', new[] { verdict, hint, retry }.Where(value => value is not null)),
                ChoicePrompt.From(lesson));
        }

        var feedback = verdict + "\n" + ChoiceSolution(state.Exercise, russian);
        if (attempt.Assistance == AnswerAssistance.Assisted && attempt.Outcome == ChoiceOutcome.Correct)
            feedback += russian ? "\nОтвет получен с помощью или после повторного предъявления." : "\nAnswer given with help or after seeing this question before.";
        await knowledge.AttachAsync(lesson, lesson.Topic, token);
        lesson.CompleteChoice(string.Join(", ", state.Exercise.Options.Where(option => selection.Contains(option.Id))
            .Select(option => ChoiceLabel(option, state.Exercise.Format, russian))), feedback);
        var evidence = await knowledge.RecordAnswerAsync(lesson, "Published choice key", token);
        var learner = await knowledge.StateWithAnswerAsync(evidence, token);
        var next = learner.EvidenceSummary is { Understanding: true, Application: true } && attempt.Outcome == ChoiceOutcome.Correct
            ? russian ? "Следующая проверка — открытое интервальное повторение. Напишите «что повторить»." :
                "The next check is an open spaced review. Write “what should I review?”."
            : lesson.ProgramId is not null
                ? russian ? "Напишите «дальше», чтобы объяснить принцип своими словами на новом примере по этой теме."
                    : "Write “next” to explain the principle in your own words using a new example on this topic."
            : russian ? "Напишите «ещё задание», чтобы объяснить принцип своими словами на новом примере." :
                "Write “another exercise” to explain the principle in your own words using a new example.";
        return new LearningResult.ChoiceReply(feedback + "\n\n" + next);
    }

    private static LearningResult.ChoiceReply InvalidChoice(Lesson lesson)
    {
        var russian = MarketLearningContent.InRussian(lesson.Theory);
        return new(russian ? "Выберите допустимый непустой ответ из вариантов ниже."
            : "Choose a valid, nonempty answer from the options below.", ChoicePrompt.From(lesson));
    }

    private static LearningResult ExplainChoice(Lesson lesson, LessonInteractionKind kind)
    {
        var russian = MarketLearningContent.InRussian(lesson.Theory);
        var text = kind == LessonInteractionKind.FullAnswer
            ? lesson.RevealChoice(ChoiceSolution(lesson.Choice!.Exercise, russian))
            : lesson.GiveChoiceHint();
        return text is null ? new LearningResult.HelpLimit() : new LearningResult.ChoiceReply(text, ChoicePrompt.From(lesson));
    }

    private static string ChoiceSolution(ChoiceExercise exercise, bool russian) =>
        (russian ? "Правильный выбор — " : "Correct selection — ") + string.Join(", ", exercise.Options
            .Where(option => exercise.CorrectOptions.Contains(option.Id))
            .Select(option => ChoiceLabel(option, exercise.Format, russian))) + "\n" + exercise.Explanation;

    public static string ChoiceLabel(ChoiceOption option, ChoiceFormat format, bool russian) =>
        format == ChoiceFormat.TrueFalse && russian ? option.Text == "True" ? "Верно" : "Неверно" : option.Text;
}
