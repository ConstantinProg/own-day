using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Telegram.Handling;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramKnowledgePresenter
{
    public static IReadOnlyList<string> Reviews(ReviewPage page)
    {
        if (page.Items.Count == 0) return ["No reviews due on this page. /learn_reviews or /learn_skills"];
        var entries = page.Items.Select(r => $"{r.Skill.Title}\nDue (UTC): {r.Schedule.DueAt:O}\n" +
            $"/learn_review {r.Skill.Id}\n/learn_review_later {r.Skill.Id} (1 day)\n/learn_review_skip {r.Skill.Id} (7 days)").ToList();
        if (page.Number > 1) entries.Add($"Previous: /learn_reviews {page.Number - 1}");
        if (page.HasNext) entries.Add($"Next: /learn_reviews {page.Number + 1}");
        return TelegramTextLists.Split(entries, $"Reviews due: page {page.Number}");
    }

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
        var explanation = state.Mastery switch
        {
            SkillMastery.Unknown => "Unknown: no assessed answers.",
            SkillMastery.NeedsPractice => "Needs practice: only negative AI assessments so far.",
            SkillMastery.Developing => "Developing: one or two positive AI assessments; evidence is limited.",
            SkillMastery.Conflicting => "Conflicting: both positive and negative AI assessments; further verification is needed.",
            SkillMastery.Demonstrated => "Demonstrated: at least three distinct answers assessed positively by AI, with no negative assessments.",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
        var messages = new List<string>
        {
            $"{status.Skill.Title}\nSkill: {status.Skill.Id}\n" +
            (status.Skill.Description != status.Skill.Title ? status.Skill.Description + "\n" : "") + explanation + "\n" +
            $"Answers: {state.Answers}; AI positive: {state.PositiveAssessments}; AI negative: {state.NegativeAssessments}.\n" +
            "Confidence: provisional (AI only). This does not establish long-term mastery.\n" +
            (state.RepeatedDifficulty ? "Repeated difficulty: at least two negative AI assessments. Their explanations are hypotheses, not confirmed misconceptions.\n" : "") +
            $"/learn_practice {status.Skill.Id}\n/learn_reviews" +
            (status.Review is null ? "" : $"\nNext review (UTC): {status.Review.DueAt:O}\nConsecutive successful delayed reviews: {status.Review.SuccessfulReviews} (capped at 4).")
        };
        foreach (var evidence in status.Evidence.TakeLast(3))
        {
            messages.Add($"Observed answer ({evidence.ObservedAt:O})\nLesson / operation: {evidence.LessonId}\n{evidence.Answer}");
            messages.Add($"AI interpretation: {(evidence.AssessedCorrect ? "positive" : "negative")}\n" +
                $"Scheduled review: {evidence.IsReview}\nSkill checked: {evidence.SkillId}\n" +
                $"Method: {evidence.Method}\nSource: {evidence.AssessmentSource}\nBasis: {evidence.AssessmentFeedback}");
        }
        return messages;
    }
}
