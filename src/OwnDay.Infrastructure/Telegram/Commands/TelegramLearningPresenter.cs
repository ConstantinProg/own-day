using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramLearningPresenter
{
    public static IReadOnlyList<TelegramLearningReply> Render(LearningResult result, bool inRussian = false) => result switch
    {
        LearningResult.Material material => RenderMaterial(material),
        LearningResult.Assessment assessment => [TelegramLearningReply.WithActions(RenderAnswer(assessment),
            LearningActionProvider.ForAssessment(assessment.LessonId, assessment.Continuation), assessment.InRussian),
            .. assessment.IndependentPracticeSkillId is not null && assessment.Continuation is not LearningContinuation.Practice
                ? new[] { assessment.InRussian ? "Для самостоятельной проверки напишите «ещё задание»."
                    : "Write “another exercise” to try it on your own." } : Array.Empty<string>(),
            .. assessment.ProjectActionId is long actionId ? new[] { ProjectPracticeNotice(actionId, assessment.InRussian) } : Array.Empty<string>()],
        LearningResult.InvalidTopic => [inRussian ? "Укажите тему занятия длиной до 500 символов." : "Enter a lesson topic of up to 500 characters."],
        LearningResult.InvalidAnswer => [inRussian ? "Напишите ответ длиной до 3000 символов." : "Write an answer of up to 3000 characters."],
        LearningResult.NoActiveLesson => [inRussian ? "Сейчас нет открытого упражнения. Напишите «Продолжим»." : "There is no open exercise. Write “continue” to get started."],
        LearningResult.ProviderUnavailable => [inRussian ? "Подготовить ответ пока не удалось. Повторите запрос чуть позже."
            : "I couldn't prepare a response. Try again in a moment."],
        LearningResult.Session session => RenderSession(session),
        LearningResult.Explanation explanation => RenderExplanation(explanation),
        LearningResult.AssessmentClarification clarification => [TelegramLearningReply.WithActions(inRussian
            ? (clarification.PartiallyCorrect ? "Часть ответа верна. " : "") + "Уточните оставшуюся часть задания.\n" + clarification.Criterion
            : (clarification.PartiallyCorrect ? "Part of your answer is correct. " : "") + "Clarify the remaining part.\n" + clarification.Criterion,
            LearningActionProvider.ForExercise(), inRussian)],
        LearningResult.AssessmentCorrection correction => [TelegramLearningReply.WithActions(correction.Guidance + "\n\n" + correction.Question +
            (inRussian ? "\nМожно ответить ещё раз или попросить объяснение." : "\nTry again or ask for an explanation."),
            LearningActionProvider.ForCorrection(correction.AnotherHintAvailable), inRussian)],
        LearningResult.HelpLimit => [inRussian ? "Подсказки к заданию закончились. Можно попросить объяснить принцип или показать решение."
            : "You've used the exercise hints. You can ask for an explanation of the idea or see the solution."],
        LearningResult.InvalidClarification => [inRussian ? "Напишите вопрос длиной до 1000 символов." : "Write a question of up to 1000 characters."],
        LearningResult.InvalidTransition => [inRussian ? "Это действие сейчас недоступно. Напишите «Продолжим», чтобы вернуться к занятию."
            : "That action is unavailable right now. Write “continue” to return to the lesson."],
        LearningResult.FormatUnavailable => [inRussian ? "Для этой проверки пока оставим текущий формат. Можно попросить пример или ответить настолько кратко, насколько позволяет вопрос."
            : "We'll keep the current format for this check. You can ask for an example or give as short an answer as the question allows."],
        LearningResult.ConceptReply concept => [concept.Text, .. SourceNotice(concept.Provenance, inRussian)],
        LearningResult.Finished => [inRussian ? "Упражнение завершено без оценки." : "Lesson finished without an assessment."],
        LearningResult.SkillNotFound => ["Skill not found. /learn_skills"],
        LearningResult.ReviewNotDue => ["This skill has no review due yet. /learn_reviews or /learn_status <skill id>"],
        LearningResult.ReviewDeferred deferred => [$"Review rescheduled to {deferred.DueAt:O} (UTC). No assessment or penalty. /learn_reviews"],
        LearningResult.ChoiceReply choice => RenderChoiceReply(choice),
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    private static IReadOnlyList<TelegramLearningReply> RenderChoiceReply(LearningResult.ChoiceReply choice)
    {
        var russian = choice.Prompt?.InRussian ?? MarketLearningContent.InRussian(choice.Message);
        var sources = SourceNotice(choice.Provenance, russian);
        if (choice.Prompt is { } prompt)
        {
            var question = TelegramChoicePresenter.Render(prompt);
            var feedback = choice.Message == question.Text ? "" : choice.Message;
            var combined = string.Join("\n\n", sources.Prepend(feedback).Append(question.Text).Where(text => text.Length > 0));
            if (combined.Length <= 4096) return [question with { Text = combined }];
            return feedback.Length == 0 ? [.. sources, question] : [feedback, .. sources, question];
        }
        if (choice.Continuation is { } continuation)
            return [TelegramLearningReply.WithActions(choice.Message + ChoiceContinuationFallback(choice),
                LearningActionProvider.ForAssessment(choice.AssessedLessonId, continuation), russian), .. sources];
        return [choice.Message, .. sources];
    }

    internal static IReadOnlyList<TelegramLearningReply> RenderAnswerWithFollowUp(LearningResult result,
        LearningAnswerFollowUp followUp, bool russian = false)
    {
        var next = followUp switch
        {
            { Practice: { } practice } => TelegramPracticePresenter.Render(practice, compact: true),
            { Route: { } route } => TelegramCurriculumPresenter.Render(route, russian),
            { Lesson: { } lesson } => Render(lesson, russian),
            _ => null
        };
        if (next is null) return Render(result, russian);
        var feedback = result switch
        {
            LearningResult.ChoiceReply choice => choice.Message,
            LearningResult.Assessment assessment => RenderAnswer(assessment with { Continuation = null }, showReview: false),
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
        if (result is LearningResult.Assessment { ProjectActionId: { } project, InRussian: var inRussian })
            feedback += "\n\n" + ProjectPracticeNotice(project, inRussian);
        var assessedId = result is LearningResult.Assessment assessmentResult ? assessmentResult.LessonId
            : (result as LearningResult.ChoiceReply)?.AssessedLessonId;
        LearningAction[] review = assessedId is { } id ? [new(LearningActionKind.ExplainAssessment, id)] : [];
        var combined = string.Join("\n\n", next.Select(reply => reply.Text).Prepend(feedback));
        var actions = next.Where(reply => reply.Actions is { Count: > 0 }).Take(2).ToArray();
        if (combined.Length <= 4096 && actions.Length <= 1)
            return [actions.Length == 0 ? TelegramLearningReply.WithActions(combined, review, russian)
                : actions[0] with { Text = combined, Actions = [.. actions[0].Actions!, .. review] }];
        return [TelegramLearningReply.WithActions(feedback, review, russian), .. next];
    }

    private static string RenderAnswer(LearningResult.Assessment assessment, bool showReview = true)
    {
        var russian = assessment.InRussian;
        var verdict = assessment.Status switch
        {
            AnswerAssessmentStatus.Conflict => russian ? "Проверки разошлись. Зачёт пока под вопросом." : "Conflict: the checks disagree. Credit is uncertain.",
            AnswerAssessmentStatus.Inconclusive => russian ? "Пока недостаточно данных для зачёта." : "There is not enough information to award credit.",
            _ => assessment.IsCorrect
                ? russian ? "Верно, упражнение зачтено." : "Correct. Exercise completed."
                : russian ? "Упражнение пока не зачтено." : "The exercise has not been credited."
        };
        var assistance = assessment.Assistance == AnswerAssistance.Assisted
            ? russian ? "\nОтвет получен с помощью." : "\nThe answer was given with help." : "";
        var next = assessment.Continuation switch
        {
            LearningContinuation.NextTopic =>
                russian ? "\nНапишите «дальше», чтобы перейти к следующей теме." : "\nWrite “next” to continue to the next topic.",
            LearningContinuation.Review =>
                russian ? "\nСледующая проверка навыка — на повторении. Напишите «что повторить», чтобы узнать, какие повторения доступны."
                    : "\nThe next check is a review. Write “what should I review” to see available reviews.",
            LearningContinuation.Practice => russian ? "\nНапишите «ещё задание», чтобы проверить навык на новом вопросе."
                : "\nWrite “another exercise” for a new question on this skill.",
            _ => ""
        };
        var review = showReview && (assessment.Criteria is not null || !assessment.IsCorrect)
            ? russian ? "\nМожно спросить «почему такая оценка»." : "\nYou can ask “why this result?”." : "";
        return verdict + "\n" + assessment.Feedback + assistance + next + review;
    }

    private static IReadOnlyList<TelegramLearningReply> RenderMaterial(LearningResult.Material material)
    {
        var russian = MarketLearningContent.InRussian(material.Theory);
        var theory = material.Topic + "\n\n" + material.Theory;
        if (material.Choice is { } choice)
            return [.. material.IncludeTheory ? new[] { theory } : [], .. SourceNotice(material.Provenance, russian),
                TelegramChoicePresenter.Render(choice)];
        if (material.Content is { Kind: LearningContentKind.ReadingChoice or LearningContentKind.OpenWriting } content)
            theory += russian ? "\n\nЦель — " + content.Objective : "\n\nObjective — " + content.Objective;
        return [.. material.IncludeTheory ? new[] { theory } : [], .. SourceNotice(material.Provenance, russian),
            .. ContextNotices(material.ProjectActionId, material.Transfer, russian),
            TelegramLearningReply.WithActions(RenderTask(material.Question, material.Content) + "\n\n" + AnswerAction(russian),
                LearningActionProvider.ForExercise(), russian)];
    }

    private static IReadOnlyList<TelegramLearningReply> RenderExplanation(LearningResult.Explanation explanation)
    {
        var russian = MarketLearningContent.InRussian(explanation.Text);
        return [explanation.Text, .. SourceNotice(explanation.Provenance, russian),
            TelegramLearningReply.WithActions(RenderTask(explanation.Question, explanation.TaskContent) + "\n\n" + AnswerAction(explanation.InRussian ?? russian),
                LearningActionProvider.ForExercise(), explanation.InRussian ?? russian)];
    }

    private static IEnumerable<string> ContextNotices(long? projectActionId, InterdisciplinaryPractice? transfer, bool russian)
    {
        if (projectActionId is long id) yield return ProjectPracticeNotice(id, russian);
        if (transfer is not null)
            yield return russian
                ? $"Потренируем общий навык в новой области — {transfer.TargetDiscipline}."
                : $"Apply the shared skill in a new area — {transfer.TargetDiscipline}.";
    }

    private static string ProjectPracticeNotice(long id, bool russian) => russian
        ? $"Учебная практика по задаче №{id}. Выполнение задания в проекте отмечается отдельно."
        : $"Educational practice related to task #{id}. Complete the actual project task separately.";

    public static string RenderAssessment(AnswerAssessmentStatus status, bool? deterministic) => status switch
    {
        AnswerAssessmentStatus.Conflict => $"Conflict: AI and {NormalizedTextAnswerCheck.MethodName} disagree (reference match: {deterministic}). Uncertain; not credited. Additional practice is required.",
        AnswerAssessmentStatus.Agreement => $"AI and {NormalizedTextAnswerCheck.MethodName} agree (reference match: {deterministic}). The AI reference is not independently verified.",
        AnswerAssessmentStatus.Inconclusive => "The available answer does not resolve all criteria. No credit was recorded.",
        AnswerAssessmentStatus.Deterministic => "The selection was checked against the published choices.",
        _ => "AI only; no deterministic check. Uncertain."
    };

    private static string[] SourceNotice(LessonProvenance? source, bool russian)
    {
        var notice = source?.Status switch
        {
            MaterialSourceStatus.VerifiedClaim => russian
                ? $"{source.VerifiedStatement}\nИсточник — {source.Reference}. Проверено {source.AccessedAt:yyyy-MM-dd}."
                : $"{source.VerifiedStatement}\nSource — {source.Reference}. Checked {source.AccessedAt:yyyy-MM-dd}.",
            MaterialSourceStatus.Unverified => russian
                ? "Актуальные детали не удалось подтвердить. Здесь можно опираться на общий принцип."
                : "The current details could not be verified. The explanation covers the general principle.",
            MaterialSourceStatus.Unavailable => russian
                ? $"Источник недоступен — {source.Reference}. Сведения по нему не удалось проверить."
                : $"Source unavailable — {source.Reference}. Its details could not be verified.",
            MaterialSourceStatus.QuoteMatched => russian
                ? $"Фрагмент сверён с источником — {source.Reference}. Проверка охватывает только этот фрагмент.\n{source.Quote}"
                : $"Excerpt matched in source — {source.Reference}. Only the excerpt was checked.\n{source.Quote}",
            MaterialSourceStatus.Retrieved => russian
                ? $"В источнике {source.Reference} не найдено подтверждение нужного фрагмента."
                : $"The source {source.Reference} contained no matching excerpt.",
            _ => null
        };
        return notice is null ? [] : [notice];
    }

    private static IReadOnlyList<TelegramLearningReply> RenderSession(LearningResult.Session session)
    {
        var russian = MarketLearningContent.InRussian(session.Theory ?? session.Topic + session.Question);
        if (session.PendingExerciseId is { } lessonId)
        {
            var paused = session.Status == LessonStatus.Paused;
            var next = paused
                ? russian ? "Занятие на паузе. Напишите «Продолжим»." : "Lesson paused. Write “continue” when you're ready."
                : russian ? "Напишите вопрос или «к упражнению»." : "Ask a question or write “start exercise”.";
            return [TelegramLearningReply.WithActions($"{session.Topic}\n\n{session.Theory}\n\n{next}",
                    paused ? [new(LearningActionKind.Continue)] : LearningActionProvider.ForTheory(lessonId, session.TheoryExpanded), russian),
                .. SourceNotice(session.Provenance, russian),
                .. session.LastExplanation is { } explanation ? new[] { explanation } : []];
        }
        if (session.Choice is { } choice)
            return [.. session.LastExplanation is { } last ? new[] { last } : [],
                .. SourceNotice(session.LastExplanationProvenance, choice.InRussian), TelegramChoicePresenter.Render(choice)];
        if (session.Content?.Kind is LearningContentKind.ReadingChoice or LearningContentKind.OpenWriting)
            return RenderRecap(session, !russian);
        var guidance = session.LastExplanation is { } saved
            ? (russian ? "\n\nПоследняя подсказка\n" : "\n\nLast hint\n") + saved[..Math.Min(saved.Length, 500)] : "";
        var action = session.Status == LessonStatus.Paused
            ? russian ? "Упражнение на паузе. Напишите «Продолжим»." : "Lesson paused. Write “continue” when you're ready."
            : AnswerAction(russian);
        var content = session.IsClarifyingAnswer ? null : session.Content;
        return [TelegramLearningReply.WithActions($"{session.Topic}\n{RenderTask(session.Question, content)}{guidance}\n\n{action}",
            SessionActions(session), russian)];
    }

    internal static IReadOnlyList<TelegramLearningReply> RenderRecap(LearningResult.Session session, bool english)
    {
        if (session.PendingExerciseId is not null || session.Choice is not null) return RenderSession(session);
        var action = session.Status == LessonStatus.Paused
            ? english ? "Write “continue” to return to this exercise." : "Напишите «Продолжим», чтобы вернуться к этому вопросу."
            : english ? "Write your answer or ask for help." : "Напишите ответ или попросите помощь.";
        var guidance = string.IsNullOrWhiteSpace(session.LastExplanation) ? Array.Empty<string>()
            : new[] { (english ? "Earlier guidance\n" : "Ранее разбирали\n") + session.LastExplanation };
        var content = session.IsClarifyingAnswer ? null : session.Content;
        return [(english ? "Current exercise — " : "Текущее упражнение — ") + session.Topic,
            .. string.IsNullOrWhiteSpace(session.Theory) ? [] : new[] { session.Theory },
            .. guidance, TelegramLearningReply.WithActions(RenderTask(session.Question, content) + "\n\n" + action, SessionActions(session), !english)];
    }

    private static string RenderTask(string question, LearningContent? content) =>
        content is { Id: not LearningContent.GeneratedExerciseId, StructuredRubric.Version: 1 } legacy
        ? string.Join('\n', new[] { question }.Concat(legacy.StructuredRubric.Criteria
            .Select(criterion => criterion.Requirement).Where(requirement => !question.Contains(requirement, StringComparison.Ordinal))))
        : question;

    internal static IReadOnlyList<TelegramLearningReply> RenderPreviousTopic(Lesson? previous, Lesson? active, bool english)
    {
        if (previous is null) return [english ? "There is no earlier completed topic in this program yet."
            : "В этой программе пока нет предыдущей завершённой темы."];
        var summary = (english ? "Earlier topic — " : "Предыдущая тема — ") + previous.Topic + "\n\n" + previous.Theory;
        var next = active is null
            ? english ? "Write “continue” when you want to move on." : "Напишите «Продолжим», когда будете готовы двигаться дальше."
            : english ? "Write “continue” to return to the open exercise." : "Напишите «Продолжим», чтобы вернуться к открытому упражнению.";
        return [summary, .. SourceNotice(previous.Provenance, !english),
            TelegramLearningReply.WithActions(next, [new(LearningActionKind.Continue)], !english)];
    }

    private static IReadOnlyList<LearningAction> SessionActions(LearningResult.Session session) => session.Status == LessonStatus.Paused
        ? [new(LearningActionKind.Continue)] : LearningActionProvider.ForExercise();

    private static string ChoiceContinuationFallback(LearningResult.ChoiceReply choice)
    {
        var russian = MarketLearningContent.InRussian(choice.Message);
        return (choice.Continuation, russian) switch
        {
            (LearningContinuation.Review, true) => "\nСледующая проверка — открытое интервальное повторение. Текстом — «что повторить».",
            (LearningContinuation.Review, false) => "\nThe next check is an open spaced review. You can write “what should I review?”.",
            (LearningContinuation.NextTopic, true) => "\nСледующая проверка — объяснить принцип своими словами на новом примере. Текстом — «дальше».",
            (LearningContinuation.NextTopic, false) => "\nFor the next check, explain the principle in your own words using a new example. You can write “next”.",
            (_, true) => "\nСледующая проверка — объяснить принцип своими словами на новом примере. Текстом — «ещё задание».",
            _ => "\nFor the next check, explain the principle in your own words using a new example. You can write “another exercise”."
        };
    }

    private static string AnswerAction(bool russian) => russian ? "Напишите ответ или попросите подсказку."
        : "Write your answer or ask for a hint.";
}
