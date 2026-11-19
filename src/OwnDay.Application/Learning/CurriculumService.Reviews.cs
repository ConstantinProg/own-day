using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public sealed record ReviewSuggestion(Skill Skill, DateTimeOffset? DueAt, string? SupportsTopic, int DependentTopics);

public sealed partial class CurriculumService
{
    private const int MaxReviewSuggestions = 3;

    public async Task<IReadOnlyList<ReviewSuggestion>> SuggestReviewsAsync(UserId user, CancellationToken token)
    {
        ValidateUser(user, token);
        var due = await knowledge.ReviewsAsync(user, token);
        // Keep overdue skills in chronological order so contextual suggestions cannot starve them.
        var suggestions = due.Items.Take(MaxReviewSuggestions)
            .Select(item => new ReviewSuggestion(item.Skill, item.Schedule.DueAt, null, 0)).ToList();
        if (suggestions.Count == MaxReviewSuggestions) return suggestions;

        var program = await store.FindAsync(user, token);
        if (program is null || program.DiagnosticPending) return suggestions;
        var route = program.CurrentRoute;
        var completed = await store.CompletedTopicsAsync(user, program.Id, route.Version, token);
        var statuses = await knowledge.TopicStatusesAsync(program, token);
        token.ThrowIfCancellationRequested();
        var pending = route.Topics.Where(topic => !completed.Contains(topic.Code) && !route.IsSkipped(topic.Code)).ToArray();
        var next = program.NextTopic(completed);
        var contextual = route.Topics.Where(topic => !route.IsSkipped(topic.Code) && statuses.ContainsKey(topic.Code))
            .Select(topic => new
            {
                Topic = topic,
                Status = statuses[topic.Code],
                SupportsNext = next?.Prerequisites.Contains(topic.Code) == true,
                Dependents = pending.Count(dependent => dependent.Prerequisites.Contains(topic.Code))
            })
            .Where(item => (item.SupportsNext || item.Dependents >= 2) && item.Status.Evidence.Count > 0 &&
                item.Status.State.Mastery != SkillMastery.Demonstrated &&
                (item.Status.Review is null || !knowledge.IsDue(item.Status.Review) &&
                    ReviewSchedule.Reconstruct(item.Status.Evidence)?.DueAt >= item.Status.Review.DueAt) &&
                suggestions.All(suggestion => suggestion.Skill.Id != item.Status.Skill.Id))
            .OrderByDescending(item => item.SupportsNext).ThenByDescending(item => item.Dependents).ThenBy(item => item.Topic.Code, StringComparer.Ordinal)
            .FirstOrDefault();
        if (contextual is not null)
            suggestions.Add(new(contextual.Status.Skill, null, contextual.SupportsNext ? next!.Title : null, contextual.Dependents));
        return suggestions;
    }
}
