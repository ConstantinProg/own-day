using System.Text.Json.Serialization;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

[JsonConverter(typeof(JsonStringEnumConverter<TeachingAction>))]
public enum TeachingAction
{
    Hint,
    Explain,
    Simplify,
    Practice,
    ReviewPrerequisite,
    Advance
}

public sealed record TeachingProposal(string Action, string TopicCode, string Reason);
public sealed record TeachingRequest(string Goal, string Discipline, IReadOnlyList<CurriculumTopic> Topics,
    string CurrentTopicCode, IReadOnlySet<string> Completed, LearnerSkillState State, string[] RecentFeedback);
public sealed record TeachingContext(TeachingAction Action, string Reason, LearnerSkillState State, string[] RecentFeedback);

public interface ITeachingPlanner
{
    Task<TeachingProposal> ProposeAsync(TeachingRequest request, CancellationToken token);
}

public static class AdaptiveTeaching
{
    public static bool TryValidate(TeachingProposal proposal, CurriculumTopic current,
        IReadOnlyList<CurriculumTopic> topics, IReadOnlySet<string> completed, LearnerSkillState state,
        out TeachingAction action)
    {
        if (!Enum.TryParse(proposal.Action, out action) || !Enum.IsDefined(action) || proposal.Action != action.ToString()) return false;
        if (string.IsNullOrWhiteSpace(proposal.Reason) || proposal.Reason.Length > 250) return false;
        var target = topics.SingleOrDefault(t => t.Code == proposal.TopicCode);
        if (target is null || !target.Prerequisites.All(completed.Contains)) return false;
        if (action == TeachingAction.ReviewPrerequisite)
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
        return action switch
        {
            TeachingAction.Hint or TeachingAction.Explain or TeachingAction.Simplify or TeachingAction.Practice => true,
            TeachingAction.Advance => !state.RepeatedDifficulty && state.Mastery is not (SkillMastery.NeedsPractice or SkillMastery.Conflicting),
            _ => false
        };
    }
}
