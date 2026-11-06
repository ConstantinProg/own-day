using OwnDay.Domain;
using OwnDay.Domain.Actions;
using OwnDay.Domain.Learning;
using OwnDay.Domain.Projects;

namespace OwnDay.Application.Learning;

public sealed record ProjectTopicRecommendation(CurriculumTopic Topic, SkillStatus? Status, bool Available);
public sealed record ProjectActionOption(long Id, string Title);
public sealed record ProjectLearningReport(LearningProgram Program, Project Project,
    IReadOnlyList<ProjectTopicRecommendation> Topics, IReadOnlyList<ProjectActionOption> Actions, int Page, bool HasNext);

public sealed partial class CurriculumService
{
    public async Task<CurriculumResult> ProjectReportAsync(UserId user, int page, CancellationToken token)
    {
        ValidateUser(user, token);
        if (page is < 1 or > MaxPageNumber) return new CurriculumResult.InvalidInput();
        var program = await store.FindAsync(user, token);
        if (program is null) return new CurriculumResult.NoProgram();
        if (program.ProjectId is not long projectId) return new CurriculumResult.NoProject();
        var project = await projects.FindProjectAsync(user, projectId, token);
        if (project is null) return new CurriculumResult.ProjectNotFound();
        var completed = await store.CompletedTopicsAsync(user, program.Id, program.CurrentRoute.Version, token);
        var topics = new List<ProjectTopicRecommendation>();
        foreach (var topic in program.CurrentRoute.Topics)
        {
            var status = await knowledge.TopicStatusAsync(user, program.Id, program.CurrentRoute.Version, topic.Code, token);
            topics.Add(new(topic, status, !program.DiagnosticPending && topic.Prerequisites.All(completed.Contains)));
        }
        var items = (await actions.GetActiveAsync(user, token)).Where(a => a.ProjectId == projectId)
            .OrderBy(a => a.Id).Skip((page - 1) * PageSize).Take(PageSize + 1).ToArray();
        token.ThrowIfCancellationRequested();
        return new CurriculumResult.ProjectReport(new(program, project, topics,
            items.Take(PageSize).Select(a => new ProjectActionOption(a.Id, a.Title)).ToArray(), page, items.Length > PageSize));
    }

    public async Task<CurriculumResult> ProjectPracticeAsync(UserId user, string code, long actionId, CancellationToken token)
    {
        ValidateUser(user, token);
        code = code.Trim();
        if (code.Length is < 1 or > CurriculumTopic.MaxCodeLength || actionId <= 0) return new CurriculumResult.InvalidInput();
        var program = await store.FindAsync(user, token);
        if (program is null) return new CurriculumResult.NoProgram();
        if (program.ProjectId is not long projectId) return new CurriculumResult.NoProject();
        var project = await projects.FindProjectAsync(user, projectId, token);
        if (project is null) return new CurriculumResult.ProjectNotFound();
        if (project.Status != ProjectStatus.Active) return new CurriculumResult.ProjectCompleted();
        var action = await actions.FindAsync(user, actionId, token);
        if (action is null || action.ProjectId != projectId || action.Status != ActionStatus.Active)
            return new CurriculumResult.ProjectActionNotFound();
        if (program.DiagnosticPending) return new CurriculumResult.Program(program);
        var topic = program.CurrentRoute.Topics.SingleOrDefault(t => t.Code == code);
        var completed = await store.CompletedTopicsAsync(user, program.Id, program.CurrentRoute.Version, token);
        if (topic is null || !topic.Prerequisites.All(completed.Contains)) return new CurriculumResult.InvalidInput();
        if (await lessons.FindActiveAsync(user, token) is not null) return new CurriculumResult.LessonInProgress();
        try
        {
            var status = await knowledge.TopicStatusAsync(user, program.Id, program.CurrentRoute.Version, code, token);
            var sharedLink = (await knowledge.LinksAsync(program, token)).SingleOrDefault(l => l.TopicCode == code);
            var shared = sharedLink is not null && status is not null ? new SharedSkillContext(status.Skill.Description, status.State) : null;
            var material = await teacher.GenerateAsync(new(topic.Title,
                new(program.Discipline, program.Goal, program.Experience, program.DiagnosticQuestion, program.DiagnosticAnswer),
                new(TeachingAction.Practice, "User selected practice related to a project task; this is educational work only.",
                    status?.State ?? LearnerSkillState.Evaluate([]), []), SharedSkill: shared,
                ProjectPractice: new(project.Id, project.Title, action.Id, action.Title)), token);
            token.ThrowIfCancellationRequested();
            var lesson = Lesson.Start(user, topic.Title, material.Theory, material.Question, material.Provenance, material.ExpectedAnswer);
            lesson.LinkToProgram(program, code);
            lesson.LinkToProjectAction(program, action.Id);
            if (status is not null) lesson.LinkToSkill(status.Skill);
            else await knowledge.AttachAsync(lesson, $"{topic.Title}\nDiscipline: {program.Discipline}\nLearning goal: {program.Goal}", token);
            token.ThrowIfCancellationRequested();
            lessons.Add(lesson);
            return new CurriculumResult.Lesson(LearningService.Material(lesson));
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new CurriculumResult.ProviderUnavailable();
        }
    }
}
