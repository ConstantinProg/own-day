using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramAssessmentReviewPresenter
{
    public static IReadOnlyList<string> Render(AssessmentReviewResult result, bool english = false) => result switch
    {
        AssessmentReviewResult.Detail detail => RenderDetail(detail, english),
        AssessmentReviewResult.Unavailable => [english ? "The review is temporarily unavailable. Your existing result is saved."
            : "Проверка оценки временно недоступна. Действующее решение сохранено. Повторите запрос позже."],
        AssessmentReviewResult.NotFound => [english ? "There is no saved assessment to explain yet."
            : "Пока нет сохранённой оценки, которую можно разобрать."],
        AssessmentReviewResult.Stale => [english ? "Open the latest assessment and use its appeal details."
            : "Запрос пересмотра устарел, уже обработан или неполон. Откройте разбор и укажите урок, текущую версию и аргумент."],
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    private static IReadOnlyList<string> RenderDetail(AssessmentReviewResult.Detail detail, bool english)
    {
        var evidence = detail.Attempt.Evidence;
        if (detail.Attempt.Lesson.Choice is { } choice)
        {
            return [detail.Attempt.Lesson.Question, .. choice.Attempts.Select((attempt, index) =>
            {
                var options = string.Join(", ", choice.Exercise.Options.Where(option => attempt.Selected.Contains(option.Id))
                    .Select(option => LearningService.ChoiceLabel(option, choice.Exercise.Format, !english)));
                var result = (english, attempt.Outcome) switch
                {
                    (true, ChoiceOutcome.Correct) => "correct",
                    (true, ChoiceOutcome.Partial) => "partially correct",
                    (true, _) => "incorrect",
                    (false, ChoiceOutcome.Correct) => "верно",
                    (false, ChoiceOutcome.Partial) => "частично верно",
                    _ => "неверно"
                };
                return (english ? "Attempt " : "Попытка ") + (index + 1) + " — " + result + "\n" + options;
            }), evidence.AssessmentFeedback];
        }
        var messages = new List<string>
        {
            $"{detail.Attempt.Lesson.Topic}\n{detail.Attempt.Lesson.Question}\n\n" +
            (english ? "Original result — " : "Исходная оценка — ") + Credit(evidence.AssessedCorrect, evidence.AssessmentStatus, english) + ".\n" +
            (english ? "Current result — " : "Действующая оценка — ") + Credit(evidence.EffectiveCorrect, evidence.EffectiveAssessmentStatus, english) + ".",
            (english ? "Your answer\n" : "Ответ\n") + evidence.Answer,
            (english ? "Why it received that result\n" : "Основание исходной оценки\n") + evidence.AssessmentFeedback
        };
        if (evidence.RubricEvidence?.Evaluation is { } evaluation &&
            evidence.Content?.StructuredRubric is { } rubric)
        {
            if (evaluation.InitialAnswer != evidence.Answer)
                messages.Add((english ? "First reply\n" : "Исходная реплика\n") + evaluation.InitialAnswer);
            messages.Add((english ? "Exercise criteria\n" : "Сохранённые критерии\n") +
                string.Join("\n\n", rubric.Criteria.Select(criterion =>
                {
                    var mark = evaluation.Assessment.Criteria.Single(item => item.Code == criterion.Code);
                    return $"{criterion.Requirement}\n{mark.Reason}";
                })));
            messages.Add(english
                ? $"To appeal, describe why.\n/learn_appeal {evidence.LessonId} | {evidence.ReviewVersion} | <reason>"
                : $"Для пересмотра укажите аргумент.\n/learn_appeal {evidence.LessonId} | {evidence.ReviewVersion} | <аргумент>");
        }
        if ((detail.Revision ?? evidence.ReviewHistory?.Revisions.LastOrDefault()) is { } revision)
        {
            var outcome = revision.Outcome switch
            {
                AssessmentReviewOutcome.Changed => english ? "The assessment was updated." : "Оценка пересмотрена.",
                AssessmentReviewOutcome.Confirmed => english ? "The review confirmed the result." : "Проверка подтвердила действующую оценку.",
                _ => english ? "The review was inconclusive. You can ask for another exercise."
                    : "Однозначного вывода нет. Можно попросить ещё одно упражнение."
            };
            messages.Insert(0, outcome + "\n\n" + revision.Primary.Rationale);
            if (revision.Verification is { } verification)
                messages.Add((english ? "Second check\n" : "Повторная проверка\n") + verification.Rationale);
        }
        return messages;
    }

    private static string Credit(bool correct, AnswerAssessmentStatus status, bool english) => status is
        AnswerAssessmentStatus.Conflict or AnswerAssessmentStatus.Inconclusive ? english ? "uncertain" : "неопределённо"
        : correct ? english ? "credited" : "зачтено" : english ? "not credited" : "не зачтено";
}
