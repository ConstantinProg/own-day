using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramKnowledgePresenter
{
    public static IReadOnlyList<string> List(IReadOnlyList<Skill> skills) => skills.Count == 0
        ? ["No skills yet. /learn <topic> or /learn_next"]
        : skills.Select(s => $"{s.Title}\nSkill: {s.Id}\n/learn_status {s.Id}\n/learn_practice {s.Id}").ToArray();

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
            $"/learn_practice {status.Skill.Id}"
        };
        foreach (var evidence in status.Evidence.TakeLast(3))
        {
            messages.Add($"Observed answer ({evidence.ObservedAt:O})\nLesson / operation: {evidence.LessonId}\n{evidence.Answer}");
            messages.Add($"AI interpretation: {(evidence.AssessedCorrect ? "positive" : "negative")}\n" +
                $"Method: {evidence.Method}\nSource: {evidence.AssessmentSource}\nBasis: {evidence.AssessmentFeedback}");
        }
        return messages;
    }
}
