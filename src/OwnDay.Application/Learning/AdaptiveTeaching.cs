using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public sealed record TeachingProposal(string Action, string TopicCode, string Reason);
public sealed record TeachingRequest(string Goal, string Discipline, IReadOnlyList<CurriculumTopic> Topics,
    string CurrentTopicCode, IReadOnlySet<string> Completed, LearnerSkillState State, string[] RecentFeedback);
public sealed record TeachingContext(string Action, string Reason, LearnerSkillState State, string[] RecentFeedback);

public interface ITeachingPlanner
{
    Task<TeachingProposal> ProposeAsync(TeachingRequest request, CancellationToken token);
}

public static class AdaptiveTeaching
{
    public static bool IsAllowed(TeachingProposal proposal, CurriculumTopic current,
        IReadOnlyList<CurriculumTopic> topics, IReadOnlySet<string> completed, LearnerSkillState state)
    {
        if (string.IsNullOrWhiteSpace(proposal.Reason) || proposal.Reason.Length > 250) return false;
        var target = topics.SingleOrDefault(t => t.Code == proposal.TopicCode);
        if (target is null || !target.Prerequisites.All(completed.Contains)) return false;
        if (proposal.Action == "ReviewPrerequisite")
        {
            var pending = new Stack<string>(current.Prerequisites);
            var prerequisites = new HashSet<string>(StringComparer.Ordinal);
            while (pending.TryPop(out var code))
            {
                if (!prerequisites.Add(code)) continue;
                foreach (var prerequisite in topics.Single(t => t.Code == code).Prerequisites) pending.Push(prerequisite);
            }
            return prerequisites.Contains(target.Code);
        }
        if (target.Code != current.Code) return false;
        return proposal.Action switch
        {
            "Hint" or "Explain" or "Simplify" or "Practice" => true,
            "Advance" => !state.RepeatedDifficulty && state.Mastery is not (SkillMastery.NeedsPractice or SkillMastery.Conflicting),
            _ => false
        };
    }
}
