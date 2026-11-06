using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Domain.Projects;

namespace OwnDay.Application.Learning;

public sealed record TopicProgress(CurriculumTopic Topic, bool Completed, bool Available,
    SkillStatus? Status, bool ReviewDue, ProgramSkillLink? Link);

public sealed record LearningProgress(LearningProgram Program, bool Selected, Project? Project,
    IReadOnlyList<TopicProgress> Topics, int Page, bool HasNext);

public sealed partial class CurriculumService
{
    // One program per page keeps even a full route readable in Telegram.
    public async Task<CurriculumResult> ProgressAsync(UserId user, int page, CancellationToken token)
    {
        ValidateUser(user, token);
        if (page is < 1 or > MaxPageNumber) return new CurriculumResult.InvalidInput();
        var programs = await store.ListAsync(user, page - 1, 2, token);
        if (programs.Count == 0) return new CurriculumResult.NoProgress(page);
        var program = programs[0];
        var selected = await store.FindAsync(user, token);
        var completed = await store.CompletedTopicsAsync(user, program.Id, program.CurrentRoute.Version, token);
        var statuses = await knowledge.TopicStatusesAsync(program, token);
        var links = await knowledge.LinksAsync(program, token);
        var project = program.ProjectId is long id ? await projects.FindProjectAsync(user, id, token) : null;
        token.ThrowIfCancellationRequested();
        var topics = program.CurrentRoute.Topics.Select(topic =>
        {
            statuses.TryGetValue(topic.Code, out var status);
            return new TopicProgress(topic, completed.Contains(topic.Code),
                !program.DiagnosticPending && topic.Prerequisites.All(completed.Contains), status,
                status?.Review is not null && knowledge.IsDue(status.Review),
                links.SingleOrDefault(link => link.TopicCode == topic.Code));
        }).ToArray();
        return new CurriculumResult.Progress(new(program, selected?.Id == program.Id, project, topics, page, programs.Count > 1));
    }
}
