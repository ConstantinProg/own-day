using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramLearningPresenter
{
    private const string Actions = "/answer <answer>\n/learn_ask <question> · /learn_explain\n/learn_resume · /learn_pause · /learn_finish";

    public static IReadOnlyList<string> Render(LearningResult result) => result switch
    {
        LearningResult.Material material => RenderMaterial(material),
        LearningResult.Assessment assessment => [RenderAnswer(assessment),
            .. assessment.IndependentPracticeSkillId is Guid skillId
                ? new[] { $"Для самостоятельной проверки откройте другое задание по этому навыку. /learn_practice {skillId}" }
                : Array.Empty<string>(),
            .. assessment.ProjectActionId is long actionId ? new[] { ProjectPracticeNotice(actionId) } : Array.Empty<string>()],
        LearningResult.InvalidTopic => ["Use /learn <topic> (up to 500 characters)."],
        LearningResult.InvalidAnswer => ["Use /answer <answer> (up to 3000 characters)."],
        LearningResult.NoActiveLesson => ["Start a lesson with /learn <topic>."],
        LearningResult.ProviderUnavailable => ["The teacher is temporarily unavailable or returned invalid material. Retry the command."],
        LearningResult.Session session => [.. session.Content?.Kind is LearningContentKind.ReadingChoice or LearningContentKind.OpenWriting
                ? RenderEnglishSession(session) : new[] { RenderSession(session) },
            .. session.Transfer is not null ? new[] { TransferNotice(session.Transfer) } : Array.Empty<string>(),
            .. session.ProjectActionId is long id ? new[] { ProjectPracticeNotice(id) } : Array.Empty<string>(),
            .. session.Provenance is not null ? new[] { RenderProvenance(session.Provenance,
                MarketLearningContent.InRussian(session.Question)) } : Array.Empty<string>()],
        LearningResult.Explanation explanation => RenderExplanation(explanation),
        LearningResult.AssessmentClarification clarification => [(clarification.PartiallyCorrect ? "Часть ответа верна. " : "") +
            "Уточните оставшуюся часть задания.\n" + clarification.Criterion + "\nОтвет сохранён."],
        LearningResult.AssessmentCorrection correction => [correction.Guidance + "\n\n" + correction.Question +
            "\nПопробуйте ещё раз, поставьте упражнение на паузу или завершите его." +
            (correction.AnotherHintAvailable ? " Можно попросить подсказку." : " Лимит подсказок исчерпан."),
            "Полное решение доступно по запросу «покажи решение»."],
        LearningResult.HelpLimit => ["Лимит подсказок исчерпан. Ответьте самостоятельно, поставьте упражнение на паузу или завершите его. Можно попросить полное решение."],
        LearningResult.InvalidClarification => ["Use /learn_ask <question> (up to 1000 characters)."],
        LearningResult.InvalidTransition => ["This action is unavailable in the current lesson state. Use /learn_resume or /learn_finish."],
        LearningResult.Finished => ["Lesson finished without an assessment. /learn <topic>"],
        LearningResult.SkillNotFound => ["Skill not found. /learn_skills"],
        LearningResult.ReviewNotDue => ["This skill has no review due yet. /learn_reviews or /learn_status <skill id>"],
        LearningResult.ReviewDeferred deferred => [$"Review rescheduled to {deferred.DueAt:O} (UTC). No assessment or penalty. /learn_reviews"],
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    private static string RenderAnswer(LearningResult.Assessment assessment)
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
        var review = assessment.Criteria is not null
            ? russian ? "\nРазбор доступен по запросу «почему такая оценка»." : "" : "";
        return verdict + "\n" + assessment.Feedback + assistance + next + review;
    }

    private static IReadOnlyList<string> RenderMaterial(LearningResult.Material material)
    {
        if (material.Content is { Kind: LearningContentKind.ReadingChoice or LearningContentKind.OpenWriting } content)
            return [material.Topic + "\n\n" + material.Theory + "\n\nЦель: " + content.Objective,
                material.Question + "\n\nНапишите ответ, вопрос по упражнению или «пауза». Можно вернуться словом «Продолжим».\n" +
                "Авторский материал OwnDay: " + content.Version + ". Это учебное упражнение, не подтверждение уровня."];
        var russian = MarketLearningContent.InRussian(material.Theory + material.Question);
        var messages = new List<string> { material.Topic + "\n\n" + material.Theory,
            material.Question + Criteria(material.Content, russian) + "\n\n" +
            (russian ? "Напишите ответ. Можно попросить подсказку или написать «пауза»." : Actions) };
        if (material.ProjectActionId is long id) messages.Add(ProjectPracticeNotice(id));
        if (material.Transfer is not null) messages.Add(TransferNotice(material.Transfer));
        if (material.Provenance is not null) messages.Add(RenderProvenance(material.Provenance, russian));
        if (material.HasReferenceCheck) messages.Add(russian
            ? "Для проверки используется пример ответа от ИИ."
            : "The reference was proposed by AI and may be inaccurate.");
        return messages;
    }

    private static IReadOnlyList<string> RenderExplanation(LearningResult.Explanation explanation)
    {
        var russian = MarketLearningContent.InRussian(explanation.Text + explanation.Question);
        var source = explanation.Provenance is { } provenance ? RenderProvenance(provenance, russian)
            : russian ? "Объяснение ИИ без независимой проверки источника." : "AI explanation; no independent source verification.";
        return [explanation.Text + "\n" + source,
            russian ? "Вернёмся к заданию.\n" + explanation.Question + "\n\nНапишите ответ или задайте вопрос."
                : "Return to: " + explanation.Topic + "\n" + explanation.Question + "\n\n" + Actions];
    }

    private static string ProjectPracticeNotice(long id) =>
        $"Educational practice related to task #{id}. Your answer does not complete the real task or project. No Commitments are created. /learn_project_report";

    private static string TransferNotice(InterdisciplinaryPractice transfer) =>
        $"Interdisciplinary practice: {transfer.SourceDiscipline} -> {transfer.TargetDiscipline}.\n" +
        $"Shared skill: {transfer.SkillId}\nSource program: {transfer.SourceProgramId}, route {transfer.SourceRouteVersion}, topic {transfer.SourceTopicCode}\n" +
        $"Relation source: {transfer.Source}. Basis: {transfer.Reason}\n" +
        "Only this shared skill is assessed. The other program's progress is unchanged; the link does not prove mastery. /learn_links";

    public static string RenderAssessment(AnswerAssessmentStatus status, bool? deterministic) => status switch
    {
        AnswerAssessmentStatus.Conflict => $"Conflict: AI and {NormalizedTextAnswerCheck.MethodName} disagree (reference match: {deterministic}). Uncertain; not credited. Additional practice is required.",
        AnswerAssessmentStatus.Agreement => $"AI and {NormalizedTextAnswerCheck.MethodName} agree (reference match: {deterministic}). The AI reference is not independently verified.",
        AnswerAssessmentStatus.Inconclusive => "The available answer does not resolve all criteria. No credit was recorded.",
        _ => "AI only; no deterministic check. Uncertain."
    };

    private static string RenderProvenance(LessonProvenance source, bool russian) => russian ? source.Status switch
    {
        MaterialSourceStatus.VerifiedClaim => $"{source.VerifiedStatement}\nИсточник — {source.Reference}\nВерсия — {source.Version}. Проверено {source.AccessedAt:O}.",
        MaterialSourceStatus.Unverified => "Актуальное утверждение осталось непроверенным. Можно продолжить с общим принципом.",
        MaterialSourceStatus.None when source.Generator == "OwnDay education" => "Учебное сравнение.",
        MaterialSourceStatus.None => "Материал подготовлен ИИ. Подтверждающий источник не получен.",
        MaterialSourceStatus.Unavailable => $"Источник недоступен — {source.Reference}. Материал не проверен.",
        MaterialSourceStatus.QuoteMatched => $"Источник — {source.Reference}\nВерсия — {source.Version}\n{source.Quote}\nПроверка охватывает только этот фрагмент.",
        _ => $"Источник — {source.Reference}\nПодтверждающий фрагмент не найден."
    } : $"Material generated by {source.Generator}.\n" + (source.Status switch
    {
        MaterialSourceStatus.VerifiedClaim => $"{source.VerifiedStatement}\nSource: {source.Reference}\nVersion: {source.Version}\nVerified at: {source.AccessedAt:O}",
        MaterialSourceStatus.Unverified => "Актуальное утверждение осталось непроверенным. Можно продолжить с общим принципом. / The current claim remains unverified.",
        MaterialSourceStatus.None when source.Generator == "OwnDay education" => "Учебное сравнение / Educational comparison.",
        MaterialSourceStatus.None => "No authoritative source was retrieved. Material is unverified.",
        MaterialSourceStatus.Unavailable => $"Source unavailable: {source.Reference}\nVersion: {source.Version}. Material is unverified.",
        _ => $"Source: {source.Reference}\nVersion: {source.Version}\nAccessed: {source.AccessedAt:O}\n" +
            (source.Status == MaterialSourceStatus.QuoteMatched
                ? $"Excerpt matched in source and theory: {source.Quote}\nOnly the excerpt was matched; the lesson is not independently verified."
                : "Source retrieved; no matching excerpt. Material is unverified.")
    });

    private static string RenderSession(LearningResult.Session session)
    {
        if (MarketLearningContent.InRussian(session.Topic + session.Question))
            return $"Упражнение по теме «{session.Topic}»\n{session.Question}{Criteria(session.Content, true)}" +
                (session.LastExplanation is { } saved ? $"\n\nПодсказка\n{saved[..Math.Min(saved.Length, 500)]}" : "") +
                (session.Status == LessonStatus.Paused
                    ? "\n\nУпражнение на паузе. Напишите «Продолжим»."
                    : "\n\nНапишите ответ или попросите подсказку.");
        var (expected, actions) = session.Status switch
        {
            LessonStatus.AwaitingAnswer => ("Expected action: Answer the current question.", Actions),
            LessonStatus.Paused => ("Expected action: Resume the lesson before answering.",
                "Lesson paused. /learn_resume or /learn_finish"),
            _ => throw new ArgumentOutOfRangeException(nameof(session))
        };
        return $"Topic: {session.Topic}\n{expected}\nCurrent question: {session.Question}{Criteria(session.Content, false)}" +
            (session.LastExplanation is { } guidance ? $"\n\nSaved guidance\n{guidance[..Math.Min(guidance.Length, 500)]}" : "") + $"\n\n{actions}";
    }

    private static string Criteria(LearningContent? content, bool russian) =>
        content is { Id: not LearningContent.GeneratedExerciseId, StructuredRubric.Version: 1 } legacy
        ? (russian ? "\n\nКритерии\n" : "\n\nCriteria\n") +
            string.Join('\n', legacy.StructuredRubric.Criteria.Select(criterion => "• " + criterion.Requirement))
        : "";

    private static IReadOnlyList<string> RenderEnglishSession(LearningResult.Session session) => session.Status == LessonStatus.Paused
        ? ["Остановимся здесь. Письмо, вопрос и помощь сохранены. Напишите «Продолжим», когда будете готовы."]
        : TelegramDialoguePresenter.Render(new(OwnDay.Application.Dialogue.DialogueReplyKind.Recap,
            [session.Topic, session.Theory ?? "", session.Question, session.LastExplanation ?? "", session.Status.ToString()]));
}
