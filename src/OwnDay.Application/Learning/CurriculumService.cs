using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public interface ICurriculumStore
{
    Task<LearningProgram?> FindAsync(UserId user, CancellationToken token);
    Task<IReadOnlySet<string>> CompletedTopicsAsync(UserId user, Guid programId, int version, CancellationToken token);
    void Add(LearningProgram program);
}

public sealed record CurriculumProposal(string Discipline, CurriculumTopic[] Topics, string? DiagnosticQuestion);
public sealed record CurriculumRequest(string Goal, string Experience, string? Question, string? Answer);

public interface ICurriculumPlanner
{
    Task<CurriculumProposal> PlanAsync(CurriculumRequest request, CancellationToken token);
}

public abstract record CurriculumResult
{
    public sealed record Program(LearningProgram Value) : CurriculumResult;
    public sealed record Lesson(LearningResult Value) : CurriculumResult;
    public sealed record InvalidInput : CurriculumResult;
    public sealed record ProjectNotFound : CurriculumResult;
    public sealed record NoProgram : CurriculumResult;
    public sealed record InvalidTransition : CurriculumResult;
    public sealed record RouteCompleted : CurriculumResult;
    public sealed record ProviderUnavailable : CurriculumResult;
}

public sealed class CurriculumService(ICurriculumStore store, ICurriculumPlanner planner,
    ILessonStore lessons, ILessonTeacher teacher, StructuredItemService projects)
{
    public async Task<CurriculumResult> CreateAsync(UserId user, string goal, string experience, long? projectId, CancellationToken token)
    {
        ValidateUser(user, token);
        goal = goal.Trim();
        experience = experience.Trim();
        if (experience.Length is < 1 or > 1000 || projectId is <= 0) return new CurriculumResult.InvalidInput();
        if (projectId is long id)
        {
            var project = await projects.FindProjectAsync(user, id, token);
            if (project is null) return new CurriculumResult.ProjectNotFound();
            goal = project.Title;
        }
        if (goal.Length is < 1 or > 500) return new CurriculumResult.InvalidInput();
        var existing = await store.FindAsync(user, token);
        if (existing is not null) return new CurriculumResult.Program(existing);
        try
        {
            var proposal = await planner.PlanAsync(new(goal, experience, null, null), token);
            token.ThrowIfCancellationRequested();
            var program = LearningProgram.Create(user, goal, experience, projectId,
                proposal.Discipline, proposal.Topics, proposal.DiagnosticQuestion);
            store.Add(program);
            return new CurriculumResult.Program(program);
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new CurriculumResult.ProviderUnavailable();
        }
    }

    public async Task<CurriculumResult> ShowAsync(UserId user, CancellationToken token)
    {
        ValidateUser(user, token);
        var program = await store.FindAsync(user, token);
        return program is null ? new CurriculumResult.NoProgram() : new CurriculumResult.Program(program);
    }

    public async Task<CurriculumResult> DiagnoseAsync(UserId user, string answer, CancellationToken token)
    {
        ValidateUser(user, token);
        answer = answer.Trim();
        if (answer.Length is < 1 or > 3000) return new CurriculumResult.InvalidInput();
        var program = await store.FindAsync(user, token);
        if (program is null) return new CurriculumResult.NoProgram();
        if (!program.DiagnosticPending) return new CurriculumResult.InvalidTransition();
        if (answer.Equals("skip", StringComparison.OrdinalIgnoreCase))
        {
            program.SkipDiagnostic();
            return new CurriculumResult.Program(program);
        }
        try
        {
            var proposal = await planner.PlanAsync(new(program.Goal, program.Experience, program.DiagnosticQuestion, answer), token);
            token.ThrowIfCancellationRequested();
            if (proposal.DiagnosticQuestion is not null || string.IsNullOrWhiteSpace(proposal.Discipline) || proposal.Discipline.Length > 100)
                return new CurriculumResult.ProviderUnavailable();
            program.ApplyDiagnostic(answer, proposal.Topics);
            return new CurriculumResult.Program(program);
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new CurriculumResult.ProviderUnavailable();
        }
    }

    public async Task<CurriculumResult> NextAsync(UserId user, CancellationToken token)
    {
        ValidateUser(user, token);
        var program = await store.FindAsync(user, token);
        if (program is null) return new CurriculumResult.NoProgram();
        if (program.DiagnosticPending) return new CurriculumResult.Program(program);
        var active = await lessons.FindActiveAsync(user, token);
        if (active is not null) return new CurriculumResult.Lesson(new LearningResult.Session(active.Topic, active.Question, active.Status));
        var completed = await store.CompletedTopicsAsync(user, program.Id, program.CurrentRoute.Version, token);
        var topic = program.NextTopic(completed);
        if (topic is null) return new CurriculumResult.RouteCompleted();
        try
        {
            var material = await teacher.GenerateAsync($"{program.Goal}: {topic.Title}", token);
            token.ThrowIfCancellationRequested();
            var lesson = Lesson.Start(user, topic.Title, material.Theory, material.Question);
            lesson.LinkToProgram(program, topic.Code);
            lessons.Add(lesson);
            return new CurriculumResult.Lesson(new LearningResult.Material(lesson.Theory, lesson.Question, lesson.Topic));
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new CurriculumResult.ProviderUnavailable();
        }
    }

    private static void ValidateUser(UserId user, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!user.IsValid) throw new ArgumentException("Invalid user.", nameof(user));
    }
}
