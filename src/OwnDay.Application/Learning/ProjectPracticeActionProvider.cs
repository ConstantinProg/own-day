using OwnDay.Domain.Projects;

namespace OwnDay.Application.Learning;

public static class ProjectPracticeActionProvider
{
    public static IReadOnlyList<LearningAction> ForTask(ProjectLearningReport report, long taskId)
    {
        if (report.Project.Status != ProjectStatus.Active || !report.Actions.Any(task => task.Id == taskId)) return [];

        var route = report.Program.CurrentRoute;
        var topics = report.Topics.Where(topic => topic.Available && !route.IsSkipped(topic.Topic.Code)).Take(2).ToArray();
        if (topics.Length != 1) return [];

        var index = route.Topics.ToList().FindIndex(topic => topic.Code == topics[0].Topic.Code);
        return index < 0 ? [] : [LearningAction.ForNumber(LearningActionKind.ProjectPractice, taskId, route.Version, index) with
            { Revision = LearningAction.TopicIdentity(report.Program.Id, route.Version, topics[0].Topic.Code) }];
    }
}
