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
public sealed record CurriculumRequest(string Goal, string Experience, string? Question, string? Answer, string? Discipline = null);

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
    public sealed record Adapted(LearningResult Value, string Reason) : CurriculumResult;
}

public sealed class CurriculumService(ICurriculumStore store, ICurriculumPlanner planner,
    ILessonStore lessons, ILessonTeacher teacher, StructuredItemService projects, KnowledgeService knowledge,
    ITeachingPlanner teachingPlanner)
{
    public async Task<CurriculumResult> CreateAsync(UserId user, string goal, string experience, long? projectId, CancellationToken token)
    {
        ValidateUser(user, token);
        goal = goal.Trim();
        experience = experience.Trim();
        if (experience.Length is < 1 or > LearningProgram.MaxExperienceLength || projectId is <= 0) return new CurriculumResult.InvalidInput();
        if (projectId is long id)
        {
            var project = await projects.FindProjectAsync(user, id, token);
            if (project is null) return new CurriculumResult.ProjectNotFound();
            goal = project.Title;
        }
        if (goal.Length is < 1 or > LearningProgram.MaxGoalLength) return new CurriculumResult.InvalidInput();
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
        if (answer.Length is < 1 or > LearningProgram.MaxDiagnosticAnswerLength) return new CurriculumResult.InvalidInput();
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
            var proposal = await planner.PlanAsync(new(program.Goal, program.Experience, program.DiagnosticQuestion, answer, program.Discipline), token);
            token.ThrowIfCancellationRequested();
            if (proposal.DiagnosticQuestion is not null)
                return new CurriculumResult.ProviderUnavailable();
            program.ApplyDiagnostic(answer, proposal.Discipline, proposal.Topics);
            return new CurriculumResult.Program(program);
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new CurriculumResult.ProviderUnavailable();
        }
    }

    public Task<CurriculumResult> NextAsync(UserId user, CancellationToken token) => NextAsync(user, false, null, token);

    public async Task<CurriculumResult> NextAsync(UserId user, bool adapt, string? focus, CancellationToken token)
    {
        ValidateUser(user, token);
        var program = await store.FindAsync(user, token);
        if (program is null) return new CurriculumResult.NoProgram();
        if (program.DiagnosticPending) return new CurriculumResult.Program(program);
        var active = await lessons.FindActiveAsync(user, token);
        if (active is not null) return new CurriculumResult.Lesson(new LearningResult.Session(active.Topic, active.Question, active.Status));
        var completed = await store.CompletedTopicsAsync(user, program.Id, program.CurrentRoute.Version, token);
        var topic = program.NextTopic(completed);
        if (focus is not null)
        {
            topic = program.CurrentRoute.Topics.SingleOrDefault(t => t.Code == focus);
            if (topic is null || !topic.Prerequisites.All(completed.Contains)) return new CurriculumResult.InvalidInput();
        }
        if (topic is null) return new CurriculumResult.RouteCompleted();
        try
        {
            var status = await knowledge.TopicStatusAsync(user, program.Id, program.CurrentRoute.Version, topic.Code, token);
            var state = status?.State ?? LearnerSkillState.Evaluate([]);
            TeachingContext? teaching = null;
            CurriculumRoute? revision = null;
            if (focus is not null)
            {
                revision = program.ProposeAdaptation(topic.Code, $"User selected topic {topic.Code}; goal and prerequisites preserved.");
            }
            else if (adapt || state.RepeatedDifficulty)
            {
                var feedback = status?.Evidence.TakeLast(3).Select(e => e.AssessmentFeedback[..Math.Min(e.AssessmentFeedback.Length, 500)]).ToArray() ?? [];
                var proposal = await teachingPlanner.ProposeAsync(new(program.Goal, program.Discipline, program.CurrentRoute.Topics,
                    topic.Code, completed, state, feedback), token);
                token.ThrowIfCancellationRequested();
                if (!AdaptiveTeaching.IsAllowed(proposal, topic, program.CurrentRoute.Topics, completed, state))
                    return new CurriculumResult.ProviderUnavailable();
                var reason = $"{proposal.Action}: {topic.Code} -> {proposal.TopicCode}. Evidence: {state.PositiveAssessments} positive, {state.NegativeAssessments} negative AI assessments. AI rationale: {proposal.Reason}";
                revision = program.ProposeAdaptation(proposal.TopicCode, reason);
                topic = program.CurrentRoute.Topics.Single(t => t.Code == proposal.TopicCode);
                teaching = new(proposal.Action, reason, state, feedback);
                status = await knowledge.TopicStatusAsync(user, program.Id, program.CurrentRoute.Version, topic.Code, token);
            }
            var material = await teacher.GenerateAsync(new LessonRequest(topic.Title,
                new LessonProgramContext(program.Discipline, program.Goal, program.Experience,
                    program.DiagnosticQuestion, program.DiagnosticAnswer), teaching), token);
            token.ThrowIfCancellationRequested();
            var lesson = Lesson.Start(user, topic.Title, material.Theory, material.Question);
            token.ThrowIfCancellationRequested();
            if (status is not null) lesson.LinkToSkill(status.Skill);
            else await knowledge.AttachAsync(lesson, $"{topic.Title}\nDiscipline: {program.Discipline}\nLearning goal: {program.Goal}", token);
            if (revision is not null) program.ApplyAdaptation(revision);
            lesson.LinkToProgram(program, topic.Code);
            lessons.Add(lesson);
            var result = new LearningResult.Material(lesson.Theory, lesson.Question, lesson.Topic);
            return revision is null ? new CurriculumResult.Lesson(result) : new CurriculumResult.Adapted(result, revision.Reason);
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
