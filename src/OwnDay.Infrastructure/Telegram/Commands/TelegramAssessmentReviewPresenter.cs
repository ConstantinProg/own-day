using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramAssessmentReviewPresenter
{
    public static IReadOnlyList<string> Render(AssessmentReviewResult result) => result switch
    {
        AssessmentReviewResult.Detail detail => RenderDetail(detail),
        AssessmentReviewResult.Unavailable => ["Проверка оценки временно недоступна. Действующее решение сохранено. Повторите запрос позже."],
        AssessmentReviewResult.NotFound => ["Оценка не найдена. Напишите «почему такая оценка», чтобы открыть последний разбор."],
        AssessmentReviewResult.Stale => ["Запрос пересмотра устарел, уже обработан или неполон. Откройте разбор и укажите урок, текущую версию и аргумент."],
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    private static IReadOnlyList<string> RenderDetail(AssessmentReviewResult.Detail detail)
    {
        var evidence = detail.Attempt.Evidence;
        var messages = new List<string>
        {
            $"{detail.Attempt.Lesson.Topic}\n{detail.Attempt.Lesson.Question}\n\n" +
            $"Исходная оценка — {Credit(evidence.AssessedCorrect, evidence.AssessmentStatus)}.\n" +
            $"Действующая оценка — {Credit(evidence.EffectiveCorrect, evidence.EffectiveAssessmentStatus)}. Версия {evidence.ReviewVersion}.",
            "Ответ\n" + evidence.Answer,
            "Основание исходной оценки\n" + evidence.AssessmentFeedback
        };
        if (evidence.RubricEvidence?.Evaluation is { } evaluation &&
            evidence.Content?.StructuredRubric is { } rubric)
        {
            if (evaluation.InitialAnswer != evidence.Answer)
                messages.Add("Исходная реплика\n" + evaluation.InitialAnswer);
            messages.Add("Сохранённые критерии " + evidence.Content.Version + "\n" +
                string.Join("\n\n", rubric.Criteria.Select(criterion =>
                {
                    var mark = evaluation.Assessment.Criteria.Single(item => item.Code == criterion.Code);
                    return $"{criterion.Requirement}\n{mark.Reason}";
                })));
            messages.Add($"Для пересмотра укажите аргумент.\n/learn_appeal {evidence.LessonId} | {evidence.ReviewVersion} | <аргумент>");
        }
        if ((detail.Revision ?? evidence.ReviewHistory?.Revisions.LastOrDefault()) is { } revision)
        {
            var outcome = revision.Outcome switch
            {
                AssessmentReviewOutcome.Changed => "Оценка пересмотрена.",
                AssessmentReviewOutcome.Confirmed => "Проверка подтвердила действующую оценку.",
                _ => $"Однозначного вывода нет. Можно пройти отдельное упражнение. /learn_practice {evidence.SkillId}"
            };
            messages.Insert(0, outcome + "\n\n" + revision.Primary.Rationale);
            if (revision.Verification is { } verification)
                messages.Add("Повторная проверка\n" + verification.Rationale);
        }
        return messages;
    }

    private static string Credit(bool correct, AnswerAssessmentStatus status) => status is
        AnswerAssessmentStatus.Conflict or AnswerAssessmentStatus.Inconclusive ? "неопределённо"
        : correct ? "зачтено" : "не зачтено";
}
