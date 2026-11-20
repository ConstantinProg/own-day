using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Telegram.Handling;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramKnowledgePresenter
{
    public static TelegramLearningReply ReviewAfterResume(ReviewSuggestion suggestion) =>
        TelegramLearningReply.WithActions($"После перерыва можно повторить «{suggestion.Skill.Title}». Текущий вопрос сохранён. Текстом — «что повторить».",
            [new(LearningActionKind.ReviewSuggestions)], inRussian: true);

    public static IReadOnlyList<TelegramLearningReply> Suggestions(IReadOnlyList<ReviewSuggestion> suggestions)
    {
        var heading = suggestions.Count == 0 ? "Сейчас подходящих повторений нет." : "Что можно повторить перед следующим занятием.";
        return [TelegramLearningReply.WithActions(heading + " Текстом — «дальше» для перехода к следующей теме.",
            [new(LearningActionKind.Next)], inRussian: true),
            .. suggestions.Select(item => item.DueAt is { } dueAt
                ? TelegramLearningReply.WithActions($"{item.Skill.Title}\nСрок повторения наступил {dueAt:yyyy-MM-dd HH:mm} UTC.\n" +
                    $"/learn_review {item.Skill.Id} · /learn_review_later {item.Skill.Id} · /learn_review_skip {item.Skill.Id}",
                    LearningActionProvider.ForReview(item.Skill.Id, due: true), inRussian: true)
                : TelegramLearningReply.WithActions($"{item.Skill.Title}\n" + (item.SupportsTopic is { } topic ? $"Пригодится в теме «{topic}»." :
                    $"От навыка зависят следующие темы ({item.DependentTopics}).") + $"\n/learn_practice {item.Skill.Id}",
                    LearningActionProvider.ForReview(item.Skill.Id, due: false), inRussian: true))];
    }

    public static IReadOnlyList<TelegramLearningReply> Reviews(ReviewPage page)
    {
        if (page.Items.Count == 0) return ["No reviews due on this page. /learn_reviews or /learn_skills"];
        var entries = page.Items.Select(item => TelegramLearningReply.WithActions($"{item.Skill.Title}\nDue (UTC): {item.Schedule.DueAt:O}\n" +
            $"/learn_review {item.Skill.Id}\n/learn_review_later {item.Skill.Id} (1 day)\n/learn_review_skip {item.Skill.Id} (7 days)",
            LearningActionProvider.ForReview(item.Skill.Id, due: true), inRussian: false));
        var replies = new List<TelegramLearningReply> { $"Reviews due: page {page.Number}" };
        replies.AddRange(entries);
        if (page.Number > 1) replies.Add(ReviewPageLink(page.Number - 1, "Previous"));
        if (page.HasNext) replies.Add(ReviewPageLink(page.Number + 1, "Next"));
        return replies;
    }

    private static TelegramLearningReply ReviewPageLink(int number, string label) =>
        TelegramLearningReply.WithActions($"{label} — /learn_reviews {number}",
            [LearningAction.ForNumber(LearningActionKind.ReviewsPage, number)], inRussian: false);

    public static IReadOnlyList<string> List(SkillPage page)
    {
        if (page.Skills.Count == 0)
            return [page.Number == 1 ? "No skills yet. /learn <topic> or /learn_next" : "No skills on this page. /learn_skills"];
        var entries = page.Skills.Select(s => $"{s.Title}\nSkill: {s.Id}\n/learn_status {s.Id}\n/learn_practice {s.Id}").ToList();
        if (page.Number > 1) entries.Add($"Previous: /learn_skills {page.Number - 1}");
        if (page.HasNext) entries.Add($"Next: /learn_skills {page.Number + 1}");
        return TelegramTextLists.Split(entries, $"Skills: page {page.Number}");
    }

    public static IReadOnlyList<string> Status(SkillStatus status)
    {
        var state = status.State;
        var explanation = TelegramSkillMasteryText.Explain(state.Mastery);
        var messages = new List<string>
        {
            $"{status.Skill.Title}\nSkill: {status.Skill.Id}\n" +
            (status.Skill.Description != status.Skill.Title ? status.Skill.Description + "\n" : "") + explanation + "\n" +
            $"Answers: {state.Answers}; credited positive: {state.PositiveAssessments}; negative without conflict: {state.NegativeAssessments}.\n" +
            "Confidence: provisional (AI assessments and optional reference comparisons). This does not establish long-term mastery.\n" +
            (state.RepeatedDifficulty ? "Repeated difficulty: at least two negative AI assessments. Their explanations are hypotheses, not confirmed misconceptions.\n" : "") +
            $"/learn_practice {status.Skill.Id}\n/learn_reviews" +
            (status.Review is null ? "" : $"\nNext review (UTC): {status.Review.DueAt:O}\nConsecutive successful delayed reviews: {status.Review.SuccessfulReviews} (capped at 4).")
        };
        foreach (var evidence in status.Evidence.TakeLast(3))
        {
            if (evidence.ChoiceEvidence is not null)
            {
                messages.Add(evidence.AssessmentFeedback);
                continue;
            }
            messages.Add($"Observed answer ({evidence.ObservedAt:O})\nLesson / operation: {evidence.LessonId}\n{evidence.Answer}");
            messages.Add($"AI interpretation: {((evidence.AiAssessedCorrect ?? evidence.AssessedCorrect) ? "positive" : "negative")}\n" +
                TelegramLearningPresenter.RenderAssessment(evidence.AssessmentStatus, evidence.DeterministicIsCorrect) + "\n" +
                $"Assistance: {evidence.Assistance} (Unknown is not independent).\n" +
                $"Scheduled review: {evidence.IsReview}\nSkill checked: {evidence.SkillId}\n" +
                $"Method: {evidence.Method}\nSource: {evidence.AssessmentSource}\nBasis: {evidence.AssessmentFeedback}");
            if (evidence.ReviewVersion > 0)
                messages.Add($"Effective credit: {evidence.EffectiveCorrect}; review version: {evidence.ReviewVersion}.\n" +
                    $"/learn_assessment {evidence.LessonId}");
        }
        return messages;
    }
}
