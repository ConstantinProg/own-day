using OwnDay.Application.Actions;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using System.Text.Json.Serialization;

namespace OwnDay.Application.Learning;

public interface ICurriculumStore
{
    Task<LearningContext?> FindContextAsync(UserId user, CancellationToken token);
    Task<Lesson?> FindPracticeLessonAsync(UserId user, Guid lessonId, CancellationToken token);
    Task<Lesson?> FindLastProgramLessonAsync(UserId user, Guid programId, CancellationToken token);
    Task<Lesson?> FindSuspendedLessonAsync(UserId user, Guid programId, CancellationToken token);
    // Flush within the caller's transaction, never commit or invoke providers.
    Task FlushAsync(CancellationToken token);
    Task<LearningProgram?> FindAsync(UserId user, CancellationToken token);
    Task<LearningProgram?> FindAsync(UserId user, Guid programId, CancellationToken token);
    Task<IReadOnlyList<LearningProgram>> ListAsync(UserId user, int offset, int limit, CancellationToken token);
    Task SelectAsync(LearningProgram program, CancellationToken token);
    Task<IReadOnlySet<string>> CompletedTopicsAsync(UserId user, Guid programId, int version, CancellationToken token);
    void Add(LearningProgram program);
}

public sealed record CurriculumProposal(string Discipline, CurriculumTopic[] Topics, string? DiagnosticQuestion,
    string? DiagnosticInterpretation = null, DiagnosticStartingLevel? StartingLevel = null);
public sealed record CurriculumRequest(string Goal, string Experience, string? Question, string? Answer, string? Discipline = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticQuestionContext[]? PriorObservations = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SelfReportedUncertainty { get; init; }
}
public sealed record DiagnosticQuestionContext(string Question, string Interpretation);

public interface ICurriculumPlanner
{
    Task<CurriculumProposal> PlanAsync(CurriculumRequest request, CancellationToken token);
}

public abstract record CurriculumResult
{
    public sealed record Program(LearningProgram Value, string? ResumeNotice = null) : CurriculumResult;
    public sealed record Lesson(LearningResult Value) : CurriculumResult
    {
        public string? Introduction { get; init; }
    }
    public sealed record InvalidInput : CurriculumResult;
    public sealed record ProjectNotFound : CurriculumResult;
    public sealed record NoProgram : CurriculumResult;
    public sealed record InvalidTransition : CurriculumResult;
    public sealed record RouteCompleted : CurriculumResult;
    public sealed record ProviderUnavailable : CurriculumResult;
    public sealed record Programs(IReadOnlyList<LearningProgram> Items, int Page, bool HasNext) : CurriculumResult;
    public sealed record ProgramNotFound : CurriculumResult;
    public sealed record LessonInProgress : CurriculumResult;
    public sealed record Linked(ProgramSkillLink Link) : CurriculumResult;
    public sealed record Links(IReadOnlyList<ProgramSkillLink> Items) : CurriculumResult;
    public sealed record Adapted(LearningResult Value, string Reason) : CurriculumResult;
    public sealed record ProjectReport(ProjectLearningReport Value) : CurriculumResult;
    public sealed record NoProject : CurriculumResult;
    public sealed record ProjectCompleted : CurriculumResult;
    public sealed record ProjectActionNotFound : CurriculumResult;
    public sealed record TransferUnavailable : CurriculumResult;
    public sealed record Progress(LearningProgress Value) : CurriculumResult;
    public sealed record Diagnostic(LearningProgram Value, string? Notice = null) : CurriculumResult;
    public sealed record SubjectDiagnosticQuestion(LearningProgram Value, string? Notice = null) : CurriculumResult;
    public sealed record NoProgress(int Page) : CurriculumResult;
    public sealed record RouteView(CurriculumRoute Route, bool Full, string? OptionalCheck = null) : CurriculumResult;
    public sealed record RouteHistory(IReadOnlyList<CurriculumRoute> Routes) : CurriculumResult;
    public sealed record RouteEditRejected : CurriculumResult;
    public sealed record GoalUpdateOffered(CurriculumGoal Goal, CurriculumRoute Route, string Snapshot, bool RetainsRouteCredit) : CurriculumResult;
    public sealed record GoalEditRejected : CurriculumResult;
}

public sealed partial class CurriculumService(ICurriculumStore store, ICurriculumPlanner planner,
    ILessonStore lessons, ILessonTeacher teacher, StructuredItemService projects, KnowledgeService knowledge,
    ITeachingPlanner teachingPlanner, IActionStore actions)
{
    public const int PageSize = 10;
    public const int MaxPageNumber = int.MaxValue / PageSize;

    public async Task<CurriculumResult> CreateAsync(UserId user, string goal, string experience, long? projectId, CancellationToken token,
        bool englishDiagnostic = false, bool suspendCurrent = false, string? goalCriterion = null, bool subjectDiagnostic = false)
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
        var active = await lessons.FindActiveAsync(user, token);
        if (!suspendCurrent && active is not null) return new CurriculumResult.LessonInProgress();
        var currentContext = await store.FindContextAsync(user, token);
        if (currentContext?.Practice is { Supported: false }) return new CurriculumResult.InvalidTransition();
        if (!suspendCurrent && currentContext?.Practice is { Active: true }) return new CurriculumResult.LessonInProgress();
        var current = await store.FindAsync(user, token);
        if (suspendCurrent && !await CanSuspendAsync(user, current, active, token)) return new CurriculumResult.InvalidTransition();
        try
        {
            var authored = englishDiagnostic && projectId is null && EnglishLearningContent.IsEnglishGoal(goal);
            var proposal = authored ? new CurriculumProposal("English", EnglishLearningContent.Topics(), EnglishLearningContent.ReadingQuestion)
                : await planner.PlanAsync(new(goal, experience, null, null), token);
            token.ThrowIfCancellationRequested();
            var program = LearningProgram.Create(user, goal, experience, projectId,
                proposal.Discipline, proposal.Topics, proposal.DiagnosticQuestion, goalCriterion);
            if (authored) program.OfferEnglishDiagnostic();
            else if (program.DiagnosticPending && subjectDiagnostic) program.OfferSubjectDiagnostic();
            if (suspendCurrent) await SuspendAsync(current, currentContext, active, token);
            store.Add(program);
            await store.SelectAsync(program, token);
            return new CurriculumResult.Program(program);
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new CurriculumResult.ProviderUnavailable();
        }
    }

    public async Task<CurriculumResult> ListAsync(UserId user, int page, CancellationToken token)
    {
        ValidateUser(user, token);
        if (page is < 1 or > MaxPageNumber) return new CurriculumResult.InvalidInput();
        var items = await store.ListAsync(user, (page - 1) * PageSize, PageSize + 1, token);
        return new CurriculumResult.Programs(items.Take(PageSize).ToArray(), page, items.Count > PageSize);
    }

    public async Task<CurriculumResult> SelectAsync(UserId user, Guid programId, CancellationToken token, bool suspendCurrent = false)
    {
        ValidateUser(user, token);
        var program = await store.FindAsync(user, programId, token);
        if (program is null) return new CurriculumResult.ProgramNotFound();
        var current = await store.FindAsync(user, token);
        var active = await lessons.FindActiveAsync(user, token);
        if (!suspendCurrent && current?.Id != program.Id && active is not null)
            return new CurriculumResult.LessonInProgress();
        var context = await store.FindContextAsync(user, token);
        if (context?.Practice is { Supported: false }) return new CurriculumResult.InvalidTransition();
        if (!suspendCurrent && current?.Id != program.Id && context?.Practice is { Active: true })
            return new CurriculumResult.LessonInProgress();
        var suspended = await store.FindSuspendedLessonAsync(user, program.Id, token);
        if (current?.Id != program.Id && suspendCurrent)
        {
            if (!await CanSuspendAsync(user, current, active, token)) return new CurriculumResult.InvalidTransition();
            await SuspendAsync(current, context, active, token);
        }
        else if (active is not null && suspended is not null) return new CurriculumResult.LessonInProgress();
        await store.SelectAsync(program, token);
        suspended?.RestoreForProgram(program);
        return new CurriculumResult.Program(program, suspended is null ? null : SuspendedExerciseNotice(program, suspended));
    }

    private async Task<bool> CanSuspendAsync(UserId user, LearningProgram? current, Lesson? active, CancellationToken token) =>
        active is null || current is not null && (active.ProgramId is null || active.ProgramId == current.Id) &&
            await store.FindSuspendedLessonAsync(user, current.Id, token) is null;

    private async Task SuspendAsync(LearningProgram? current, LearningContext? context, Lesson? active, CancellationToken token)
    {
        if (active is not null) active.SuspendForProgramSwitch(current!);
        if (context?.Practice is { Active: true }) context.StopPractice();
        // Any required provider snapshot has already been obtained before mutation.
        // Apply owns this flush and the final selection/reply checkpoint atomically.
        await store.FlushAsync(token);
    }

    public async Task<CurriculumResult> LinkAsync(UserId user, string topicCode, Guid skillId, string reason, CancellationToken token)
    {
        ValidateUser(user, token);
        var program = await store.FindAsync(user, token);
        if (program is null) return new CurriculumResult.NoProgram();
        var link = await knowledge.ConfirmTopicAsync(program, topicCode, skillId, reason.Trim(), token);
        return link is null ? new CurriculumResult.InvalidInput() : new CurriculumResult.Linked(link);
    }

    public async Task<CurriculumResult> LinksAsync(UserId user, CancellationToken token)
    {
        ValidateUser(user, token);
        var program = await store.FindAsync(user, token);
        return program is null ? new CurriculumResult.NoProgram() : new CurriculumResult.Links(await knowledge.LinksAsync(program, token));
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
        if (program.EnglishDiagnostic is not null) return await DiagnoseEnglishAsync(program, answer, token);
        if (program.SubjectDiagnostic is not null) return await DiagnoseSubjectAsync(program, answer, token);
        if (!program.DiagnosticPending) return new CurriculumResult.InvalidTransition();
        if (answer.Equals("skip", StringComparison.OrdinalIgnoreCase) || IsDiagnosticUncertainty(answer))
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

    private async Task<CurriculumResult> DiagnoseSubjectAsync(LearningProgram program, string answer, CancellationToken token)
    {
        var diagnostic = program.SubjectDiagnostic!;
        if (!diagnostic.Supported) return new CurriculumResult.InvalidTransition();
        if (await lessons.FindActiveAsync(program.UserId, token) is not null) return new CurriculumResult.LessonInProgress();
        if (answer.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            if (diagnostic.Deferred) program.ResumeSubjectDiagnostic();
            return program.DiagnosticPending ? new CurriculumResult.SubjectDiagnosticQuestion(program)
                : new CurriculumResult.InvalidTransition();
        }
        if (!program.DiagnosticPending) return new CurriculumResult.InvalidTransition();
        if (answer.Equals("skip", StringComparison.OrdinalIgnoreCase))
        {
            program.SkipDiagnostic();
            return new CurriculumResult.Program(program);
        }

        try
        {
            var uncertainty = IsDiagnosticUncertainty(answer);
            var proposal = await planner.PlanAsync(new(program.Goal, program.Experience, program.DiagnosticQuestion,
                answer, program.Discipline, diagnostic.Observations.Select(item => new DiagnosticQuestionContext(item.Question, item.Interpretation)).ToArray())
                { SelfReportedUncertainty = uncertainty }, token);
            token.ThrowIfCancellationRequested();
            if (proposal.DiagnosticInterpretation is null || proposal.StartingLevel is null)
                return new CurriculumResult.ProviderUnavailable();
            var substantiveAnswers = diagnostic.Observations.Count(item => !item.SelfReportedUncertainty) + (uncertainty ? 0 : 1);
            var level = substantiveAnswers < 2 && (uncertainty || diagnostic.Observations.Any(item => item.SelfReportedUncertainty))
                ? DiagnosticStartingLevel.Unknown : proposal.StartingLevel.Value;
            program.RecordSubjectObservation(answer, uncertainty ? "Самооценка неопределённости. Предметных сведений в ответе нет."
                : proposal.DiagnosticInterpretation, proposal.Discipline,
                uncertainty && substantiveAnswers == 0 ? program.CurrentRoute.Topics : proposal.Topics,
                proposal.DiagnosticQuestion, level, uncertainty);
            return program.DiagnosticPending
                ? new CurriculumResult.SubjectDiagnosticQuestion(program, uncertainty
                    ? "Вы сообщили, что пока не уверены. Попробуем более конкретный вопрос, чтобы подобрать сложность." : null)
                : new CurriculumResult.Program(program);
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new CurriculumResult.ProviderUnavailable();
        }
    }

    public Task<CurriculumResult> NextAsync(UserId user, CancellationToken token) => StartLessonAsync(user, LessonSelection.Next, null, token);

    private static bool IsDiagnosticUncertainty(string answer) => answer.Trim().TrimEnd('.', '!', '…').ToLowerInvariant() is
        "очень смутно" or "смутно" or "не уверен" or "не уверена" or "very vaguely" or "not sure";

    public async Task<CurriculumResult> CompleteDiagnosticAndStartAsync(UserId user, string answer, CancellationToken token)
    {
        var result = await DiagnoseAsync(user, answer, token);
        if (result is not CurriculumResult.Program program) return result;
        var next = await NextAsync(user, token);
        if (next is CurriculumResult.Lesson exercise && IsDiagnosticUncertainty(answer))
            return exercise with { Introduction = "Вы сообщили, что пока не уверены. Начнём с предварительной сложности и уточним её по ответам на упражнения." };
        return next is CurriculumResult.Lesson lesson && program.Value.SubjectDiagnostic is { Deferred: true }
            ? lesson with { Introduction = "Диагностика отложена. Начнём с предварительной сложности. Вернуться после упражнения можно командой /learn_diagnose start." }
            : next;
    }

    internal async Task<CurriculumResult> NextAsync(UserId user, Guid programId, CancellationToken token)
    {
        ValidateUser(user, token);
        var program = await store.FindAsync(user, programId, token);
        return program is null ? new CurriculumResult.ProgramNotFound()
            : await StartLessonAsync(user, LessonSelection.Next, null, token, program);
    }

    public Task<CurriculumResult> AdaptAsync(UserId user, CancellationToken token) => StartLessonAsync(user, LessonSelection.Adapt, null, token);

    public Task<CurriculumResult> FocusAsync(UserId user, string topicCode, CancellationToken token) => StartLessonAsync(user, LessonSelection.Focus, topicCode, token);

    private enum LessonSelection
    {
        Next,
        Adapt,
        Focus
    }

    private async Task<CurriculumResult> StartLessonAsync(UserId user, LessonSelection selection, string? focus, CancellationToken token,
        LearningProgram? target = null)
    {
        ValidateUser(user, token);
        var program = target ?? await store.FindAsync(user, token);
        if (program is null) return new CurriculumResult.NoProgram();
        if (program.EnglishDiagnostic is { Supported: false }) return new CurriculumResult.ProviderUnavailable();
        if (program.DiagnosticPending) return new CurriculumResult.Program(program);
        var active = await lessons.FindActiveAsync(user, token);
        if (active is not null) return new CurriculumResult.Lesson(LearningService.Session(active));
        var suspended = await store.FindSuspendedLessonAsync(user, program.Id, token);
        if (suspended is not null)
        {
            suspended.RestoreForProgram(program);
            return new CurriculumResult.Lesson(LearningService.Session(suspended))
                { Introduction = SuspendedExerciseNotice(program, suspended) };
        }
        var completed = await store.CompletedTopicsAsync(user, program.Id, program.CurrentRoute.Version, token);
        var route = program.CurrentRoute;
        var topic = program.NextTopic(completed);
        if (selection == LessonSelection.Focus)
        {
            topic = program.CurrentRoute.Topics.SingleOrDefault(t => t.Code == focus);
            if (topic is null || !route.IsSkipped(topic.Code) && !route.PrerequisitesSatisfied(topic, completed))
                return new CurriculumResult.InvalidInput();
        }
        if (topic is null) return new CurriculumResult.RouteCompleted();
        try
        {
            var status = await knowledge.TopicStatusAsync(user, program.Id, program.CurrentRoute.Version, topic.Code, token);
            var links = await knowledge.LinksAsync(program, token);
            var sharedLink = links.SingleOrDefault(link => link.TopicCode == topic.Code);
            var shared = sharedLink is not null && status is not null ? knowledge.SharedContext(status, sharedLink) : null;
            var state = shared?.State ?? status?.State ?? LearnerSkillState.Evaluate([]);
            TeachingContext? teaching = null;
            CurriculumRoute? revision = null;
            if (selection == LessonSelection.Focus)
            {
                revision = program.ProposeAdaptation(topic.Code, $"User selected topic {topic.Code}; goal and prerequisites preserved.");
            }
            else if (selection == LessonSelection.Adapt || state.RepeatedDifficulty)
            {
                var feedback = shared is not null ? [] : status?.Evidence.TakeLast(3)
                    .Select(e => e.AssessmentFeedback[..Math.Min(e.AssessmentFeedback.Length, 500)]).ToArray() ?? [];
                var proposal = await teachingPlanner.ProposeAsync(new(program.Goal, program.Discipline, program.CurrentRoute.Topics,
                    topic.Code, completed, state, feedback), token);
                token.ThrowIfCancellationRequested();
                var satisfied = completed.Concat(route.Path?.SkippedTopics ?? []).ToHashSet(StringComparer.Ordinal);
                if (!AdaptiveTeaching.TryValidate(proposal, topic, route.Topics, satisfied, state, out var action))
                    return new CurriculumResult.ProviderUnavailable();
                var reason = $"{proposal.Action}: {topic.Code} -> {proposal.TopicCode}. Evidence: {state.PositiveAssessments} positive, {state.NegativeAssessments} negative AI assessments. AI rationale: {proposal.Reason}";
                revision = program.ProposeAdaptation(proposal.TopicCode, reason);
                topic = program.CurrentRoute.Topics.Single(t => t.Code == proposal.TopicCode);
                teaching = new(action, reason, state, feedback);
                status = await knowledge.TopicStatusAsync(user, program.Id, program.CurrentRoute.Version, topic.Code, token);
                sharedLink = links.SingleOrDefault(link => link.TopicCode == topic.Code);
                shared = sharedLink is not null && status is not null ? knowledge.SharedContext(status, sharedLink) : null;
            }
            var personalization = await knowledge.PersonalizationAsync(user, token, program.Discipline);
            var depth = selection == LessonSelection.Focus && route.IsSkipped(topic.Code) ? CurriculumDepth.Brief : route.DepthFor(topic.Code);
            var diagnosticApplies = route.Path?.GoalChange is not { } change || change.Effective == change.Original;
            var authored = diagnosticApplies && depth == CurriculumDepth.Standard && program.EnglishDiagnostic is { Supported: true } &&
                topic.Code == EnglishLearningContent.FirstTopicCode;
            var scaffold = authored && program.EnglishDiagnostic!.Results.Any(result =>
                !result.AssessedCorrect || result.Assistance != AnswerAssistance.Independent);
            var material = authored ? new LessonMaterial(EnglishLearningContent.LessonTheory, EnglishLearningContent.LessonQuestion)
                : await teacher.GenerateAsync(new LessonRequest(topic.Title,
                new LessonProgramContext(program.Discipline, program.Goal, program.Experience,
                    diagnosticApplies ? program.DiagnosticQuestion : null, diagnosticApplies ? program.DiagnosticAnswer : null), teaching, SharedSkill: shared,
                Personalization: personalization.ForGeneration())
                {
                    RouteDepth = depth == CurriculumDepth.Standard ? null : depth
                }, token);
            if (scaffold) material = material with { Theory = material.Theory + "\n\n" + EnglishLearningContent.LessonScaffold };
            token.ThrowIfCancellationRequested();
            var lesson = material.CreateLesson(user, topic.Title);
            if (authored) lesson.SetContent(EnglishLearningContent.FirstLesson);
            if (scaffold) lesson.AddExplanation(LessonInteractionKind.Clarification, "Опора после диагностики", EnglishLearningContent.LessonScaffold);
            lesson.SetTeaching(new(teaching is null ? PedagogicalStrategy.Standard : AdaptiveTeaching.StrategyFor(teaching.Action),
                personalization.EffectivePreferences()));
            token.ThrowIfCancellationRequested();
            if (status is not null) lesson.LinkToSkill(status.Skill);
            else await knowledge.AttachAsync(lesson, $"{topic.Title}\nDiscipline: {program.Discipline}\nLearning goal: {program.Goal}", token);
            if (revision is not null) program.ApplyAdaptation(revision);
            lesson.LinkToProgram(program, topic.Code);
            lessons.Add(lesson);
            var result = LearningService.Material(lesson);
            return revision is null ? new CurriculumResult.Lesson(result) { Introduction = SharedSkillNotice(shared) } :
                new CurriculumResult.Adapted(result, revision.Reason);
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

    private static string? SuspendedExerciseNotice(LearningProgram program, Lesson lesson) =>
        lesson.ProgramId == program.Id && lesson.TopicCode is { } code && lesson.RouteVersion is { } version && !program.IsCurrentTopic(code, version)
            ? "Упражнение было открыто для другой версии маршрута. Можно ответить по сохранённым критериям или завершить его через /learn_finish, а затем открыть задание текущего маршрута через /learn_next."
            : null;
}
