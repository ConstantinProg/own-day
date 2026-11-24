using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public interface ILessonStore
{
    Task<Lesson?> FindActiveAsync(UserId user, CancellationToken token);
    Task<Lesson?> FindLatestAssessedAsync(UserId user, Guid? programId, CancellationToken token);
    Task<string?> FindPreviousChoiceQuestionAsync(UserId user, Guid? programId, string topic, CancellationToken token) =>
        Task.FromResult<string?>(null);
    Task<string[]> RecentQuestionsAsync(UserId user, Guid skillId, CancellationToken token) => Task.FromResult(Array.Empty<string>());
    Task<bool> ChoiceQuestionSeenAsync(UserId user, string question, CancellationToken token) => Task.FromResult(false);
    void Add(Lesson lesson);
}

public sealed record LessonProgramContext(string Discipline, string Goal, string Experience,
    string? DiagnosticQuestion, string? DiagnosticAnswer)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DiagnosticStartingLevel? StartingLevel { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PracticalUse { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? StartingLevelSource { get; init; }

    internal static LessonProgramContext From(LearningProgram program, bool includeDiagnostic = true)
    {
        var diagnostic = includeDiagnostic ? program.SubjectDiagnostic : null;
        (DiagnosticStartingLevel? level, string? source) = diagnostic switch
        {
            { Supported: true } when diagnostic.Observations.LastOrDefault()?.Kind == DiagnosticAnswerKind.StartWithFoundations
                => (DiagnosticStartingLevel.Foundation, "learner_requested_foundations"),
            { Supported: true, StartingLevel: not DiagnosticStartingLevel.Unknown } => (diagnostic.StartingLevel, "subject_diagnostic"),
            _ when LearnerExperience.IsBeginner(LearnerExperience.ReadSelfReport(program.Experience)) => (DiagnosticStartingLevel.Foundation, "beginner_self_report"),
            _ => ((DiagnosticStartingLevel?)null, (string?)null)
        };
        return new(program.Discipline, program.Goal, program.Experience,
            includeDiagnostic ? program.DiagnosticQuestion : null, includeDiagnostic ? program.DiagnosticAnswer : null)
        {
            StartingLevel = level,
            StartingLevelSource = source,
            PracticalUse = LearnerExperience.ReadPracticalUse(program.Experience)
        };
    }
}
public sealed record LessonRequest(string Topic, LessonProgramContext? Program = null, TeachingContext? Teaching = null,
    bool IsReview = false, SharedSkillContext? SharedSkill = null, ProjectPracticeContext? ProjectPractice = null,
    LearningPersonalization? Personalization = null, TransferPracticeContext? Transfer = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CurriculumDepth? RouteDepth { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ExerciseEvidenceKind? PracticeEvidenceKind { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string[]? PreviousQuestions { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ChoiceFormat? PreferredChoice { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ExerciseResponseFormat? RequestedFormat { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? FormatReason { get; init; }
}
public enum ExerciseResponseFormat { ShortText, SingleChoice, MultipleChoice }
public sealed record TransferPracticeContext(string SourceTopic, InterdisciplinaryPractice Relation);
public sealed record ProjectPracticeContext(long ProjectId, string ProjectTitle, long ActionId, string ActionTitle);
public sealed record SharedSkillContext(string Description, LearnerSkillState State)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? IdentityBasis { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool NeedsVerification { get; init; }
}

public sealed record LessonMaterial(string Theory, string Question)
{
    public LessonProvenance? Provenance { get; init; }
    public string? ExpectedAnswer { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public AnswerReferenceKind ReferenceKind { get; init; }
    public LearningContent? Content { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ChoiceExercise? Choice { get; init; }

    internal Lesson CreateLesson(UserId user, string topic, bool reused = false)
    {
        if (Choice is not null && (Content is not null || ExpectedAnswer is not null || Choice.Question != Question))
            throw new ArgumentException("Choice material must use its published choice key and original question.");
        var lesson = Lesson.Start(user, topic, Theory, Question, Provenance, ExpectedAnswer, Content, ReferenceKind);
        if (Choice is { } choice) lesson.PublishChoice(choice, reused);
        return lesson;
    }
}
public sealed record LessonAssessment(string Feedback, bool IsCorrect)
{
    public string Source { get; init; } = "LLM (unspecified model)";
    public RubricAssessment? Rubric { get; init; }
    public ExerciseCorrectionProposal? Correction { get; init; }
}
public sealed record LessonExplanation(string Explanation)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public LessonProvenance? Provenance { get; init; }
}
public sealed record CriterionFeedback(string Requirement, RubricCriterionResult Result, string Reason);
public enum LearningContinuation { Practice, Review, NextTopic }

public interface ILessonTeacher
{
    Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token);
    Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token);
    Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token);
    // Legacy implementations remain source-compatible. Background execution requires
    // these immutable contracts and safely rejects an adapter that does not support them.
    Task<LessonAssessment> AssessAsync(Requests.LessonSnapshot lesson, string answer, CancellationToken token) =>
        throw new LessonProviderException();
    Task<Requests.ChoiceInterpretation> InterpretChoiceAsync(Requests.ChoiceInterpretationInput input, CancellationToken token) =>
        throw new LessonProviderException();
    Task<LessonExplanation> ExplainAsync(Requests.LessonSnapshot lesson, string request, LessonInteractionKind kind, CancellationToken token) =>
        throw new LessonProviderException();
}

public enum LessonProviderFailure
{
    Unavailable,
    HttpError,
    InvalidResponse,
    Timeout,
    PermanentHttpError,
    BudgetLimit
}

public sealed class LessonProviderException(LessonProviderFailure reason = LessonProviderFailure.Unavailable, bool outcomeUnknown = false) : Exception
{
    public LessonProviderFailure Reason { get; } = reason;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
}

public abstract record LearningResult
{
    public sealed record Material(string Theory, string Question, string Topic) : LearningResult
    {
        public LearningContent? Content { get; init; }
        public LessonProvenance? Provenance { get; init; }
        public bool HasReferenceCheck { get; init; }
        public long? ProjectActionId { get; init; }
        public InterdisciplinaryPractice? Transfer { get; init; }
        public ChoicePrompt? Choice { get; init; }
        public bool IncludeTheory { get; init; } = true;
    }
    public sealed record Assessment(string Feedback) : LearningResult
    {
        public AnswerAssistance Assistance { get; init; }
        public bool IsCorrect { get; init; }
        public bool InRussian { get; init; }
        public AnswerAssessmentStatus Status { get; init; }
        public bool? DeterministicIsCorrect { get; init; }
        public long? ProjectActionId { get; init; }
        public IReadOnlyList<CriterionFeedback>? Criteria { get; init; }
        public Guid? IndependentPracticeSkillId { get; init; }
        public Guid? LessonId { get; init; }
        public LearningContinuation? Continuation { get; init; }
    }
    public sealed record InvalidTopic : LearningResult;
    public sealed record InvalidAnswer : LearningResult;
    public sealed record NoActiveLesson : LearningResult;
    public sealed record ProviderUnavailable : LearningResult;
    public sealed record Session(string Topic, string Question, LessonStatus Status) : LearningResult
    {
        public LearningContent? Content { get; init; }
        public string? Theory { get; init; }
        public string? LastExplanation { get; init; }
        public LessonProvenance? LastExplanationProvenance { get; init; }
        public LessonProvenance? Provenance { get; init; }
        public long? ProjectActionId { get; init; }
        public InterdisciplinaryPractice? Transfer { get; init; }
        public bool IsClarifyingAnswer { get; init; }
        public ChoicePrompt? Choice { get; init; }
        public Guid? PendingExerciseId { get; init; }
        public bool TheoryExpanded { get; init; }
    }
    public sealed record Explanation(string Text, string Topic, string Question, LessonProvenance? Provenance = null) : LearningResult
    {
        public bool? InRussian { get; init; }
        public LearningContent? TaskContent { get; init; }
    }
    public sealed record AssessmentClarification(string Criterion, bool PartiallyCorrect = false) : LearningResult;
    public sealed record AssessmentCorrection(string Guidance, string Question, bool AnotherHintAvailable) : LearningResult;
    public sealed record HelpLimit : LearningResult;
    public sealed record InvalidClarification : LearningResult;
    public sealed record InvalidTransition : LearningResult;
    public sealed record FormatUnavailable : LearningResult;
    public sealed record ConceptReply(string Text, LessonProvenance? Provenance) : LearningResult;
    public sealed record Finished : LearningResult;
    public sealed record SkillNotFound : LearningResult;
    public sealed record ReviewNotDue : LearningResult;
    public sealed record ReviewDeferred(DateTimeOffset DueAt) : LearningResult;
    public sealed record ChoiceReply(string Message, ChoicePrompt? Prompt = null, LearningContinuation? Continuation = null) : LearningResult
    {
        public bool IsCorrect { get; init; }
        public Guid? AssessedLessonId { get; init; }
        public LessonProvenance? Provenance { get; init; }
    }
}

public sealed partial class LearningService(ILessonStore store, ILessonTeacher teacher, KnowledgeService knowledge, Guid? operationId = null,
    ICurriculumStore? curriculum = null, bool? replyInRussian = null)
{
    public async Task<LearningResult> StartAsync(UserId user, string topic, CancellationToken token)
    {
        ValidateUser(user, token);
        topic = topic.Trim();
        if (string.IsNullOrWhiteSpace(topic) || topic.Length > 500)
        {
            return new LearningResult.InvalidTopic();
        }

        var active = await store.FindActiveAsync(user, token);
        if (active is not null)
        {
            return Session(active);
        }

        try
        {
            var program = curriculum is null ? null : await curriculum.FindAsync(user, token);
            var personalization = await knowledge.PersonalizationAsync(user, token);
            var previousChoice = await store.FindPreviousChoiceQuestionAsync(user, program?.Id, topic, token);
            var request = new LessonRequest(topic, Program: program is null ? null : LessonProgramContext.From(program),
                Personalization: personalization.ForGeneration())
            {
                PreviousQuestions = previousChoice is null ? null : [previousChoice],
                PracticeEvidenceKind = previousChoice is null ? null : ExerciseEvidenceKind.Understanding
            };
            var material = await teacher.GenerateAsync(ChoiceTeaching.SelectFormat(request), token);
            token.ThrowIfCancellationRequested();
            if (previousChoice is not null &&
                !AdaptiveTeaching.IsValidFollowUp(material, [previousChoice], ExerciseEvidenceKind.Understanding))
                return new LearningResult.ProviderUnavailable();
            var lesson = await CreateWithChoiceHistoryAsync(user, topic, material, token);
            if (program is not null) lesson.LinkPracticeToProgram(program);
            lesson.SetTeaching(new(PedagogicalStrategy.Standard, personalization.EffectivePreferences()));
            await knowledge.AttachAsync(lesson, lesson.Topic, token);
            store.Add(lesson);
            return Material(lesson);
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new LearningResult.ProviderUnavailable();
        }
    }

    public async Task<LearningResult> AnswerAsync(UserId user, string answer, CancellationToken token)
    {
        ValidateUser(user, token);
        answer = answer.Trim();
        if (string.IsNullOrWhiteSpace(answer) || answer.Length > 3000)
        {
            return new LearningResult.InvalidAnswer();
        }

        var active = await store.FindActiveAsync(user, token);
        if (active is null)
        {
            return new LearningResult.NoActiveLesson();
        }
        if (!active.AcceptsAnswer)
        {
            return new LearningResult.InvalidTransition();
        }
        if (active.Choice is { }) return await AnswerChoiceAsync(active, answer, token);

        var assessmentContent = active.AssessmentContent;
        LessonAssessment result;
        try
        {
            result = await teacher.AssessAsync(active, answer, token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result.Feedback) || result.Feedback.Length > 3000 ||
                string.IsNullOrWhiteSpace(result.Source) || result.Source.Length > 200)
                return new LearningResult.ProviderUnavailable();
            if (assessmentContent?.StructuredRubric is { } rubric)
            {
                if (result.Rubric is null) return new LearningResult.ProviderUnavailable();
                result.Rubric.Validate(rubric);
                if (result.Correction?.Kind == ExerciseCorrectionKind.Redirect &&
                    result.Rubric.Criteria.All(item => item.Result == RubricCriterionResult.NotMet))
                    return new LearningResult.AssessmentCorrection("Вернёмся к текущему вопросу. Оценка не записана.",
                        active.ResponseQuestion, active.CanOfferMoreHelp);
            }
            await knowledge.AttachAsync(active, active.Topic, token);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new LearningResult.ProviderUnavailable();
        }

        // State/evidence failures must escape so the caller rolls back the entire update transaction.
        if (assessmentContent?.StructuredRubric is not null)
        {
            active.ApplyRubricAssessment(answer, result.Feedback, result.Rubric!, operationId,
                AdaptiveTeaching.DistinctCorrection(active, result.Correction));
            if (active.RubricEvaluation is { Final: false, AwaitingCorrection: true } correction)
                return new LearningResult.AssessmentCorrection(correction.Corrections[^1].Proposal.Guidance,
                    active.ResponseQuestion, active.CanOfferMoreHelp);
            if (active.RubricEvaluation is { Final: false, Clarification: { } clarification })
                return new LearningResult.AssessmentClarification(clarification,
                    result.Rubric!.Criteria.Any(item => item.Result == RubricCriterionResult.Met));
        }
        else active.Complete(answer, result.Feedback, result.IsCorrect);
        var evidence = await knowledge.RecordAnswerAsync(active, result.Source, token);
        var state = evidence is { AssessedCorrect: true, RubricEvidence: not null }
            ? await knowledge.StateWithAnswerAsync(evidence, token) : null;
        return new LearningResult.Assessment(result.Feedback)
        {
            Status = active.AssessmentStatus, DeterministicIsCorrect = active.DeterministicIsCorrect, ProjectActionId = active.ProjectActionId,
            Assistance = active.Assistance,
            IsCorrect = evidence.AssessedCorrect,
            InRussian = MarketLearningContent.InRussian(active.Theory),
            Continuation = state switch
            {
                { Mastery: SkillMastery.Demonstrated } when active.ProgramId is not null => LearningContinuation.NextTopic,
                { Mastery: SkillMastery.Demonstrated or SkillMastery.Developing,
                    EvidenceSummary: { Understanding: true, Application: true } } => LearningContinuation.Review,
                not null => LearningContinuation.Practice,
                null when evidence.AssessedCorrect && active.ProgramId is not null => LearningContinuation.NextTopic,
                null when evidence.AssessedCorrect => LearningContinuation.Practice,
                null when AdaptiveTeaching.NeedsOpenCheck(evidence) && active.ProgramId is not null => LearningContinuation.NextTopic,
                null when AdaptiveTeaching.NeedsOpenCheck(evidence) => LearningContinuation.Practice,
                _ => null
            },
            LessonId = active.Id,
            IndependentPracticeSkillId = active.IsCorrect == true && active.Assistance == AnswerAssistance.Assisted ? active.SkillId : null,
            Criteria = assessmentContent?.StructuredRubric is { } publishedRubric && active.RubricEvaluation is { } evaluation
                ? publishedRubric.Criteria.Select(criterion =>
                {
                    var mark = evaluation.Assessment.Criteria.Single(item => item.Code == criterion.Code);
                    return new CriterionFeedback(criterion.Requirement, mark.Result, mark.Reason);
                }).ToArray()
                : null
        };
    }

    public async Task<LearningResult> PauseAsync(UserId user, CancellationToken token)
    {
        ValidateUser(user, token);
        var lesson = await store.FindActiveAsync(user, token);
        if (lesson is null) return new LearningResult.NoActiveLesson();
        if (lesson.Status != LessonStatus.AwaitingAnswer) return new LearningResult.InvalidTransition();
        lesson.Pause();
        return Session(lesson);
    }

    public Task<LearningResult> PracticeAsync(UserId user, Guid skillId, CancellationToken token) =>
        StartSkillLessonAsync(user, skillId, SkillLessonKind.Practice, token);

    internal Task<LearningResult> PracticeAfterAnswerAsync(Lesson answered, Guid skillId, CancellationToken token) =>
        StartSkillLessonAsync(answered.UserId, skillId, SkillLessonKind.Practice, token, answered.Question);

    public async Task<LearningResult> PracticeLastAsync(UserId user, Guid? programId, CancellationToken token)
    {
        ValidateUser(user, token);
        var active = await store.FindActiveAsync(user, token);
        if (active is { AcceptsAnswer: true, Choice.SolutionSeen: true, SkillId: { } exposedSkill } &&
            (active.ProgramId ?? active.PracticeProgramId) == programId)
            return await PracticeAsync(user, exposedSkill, token);
        var latest = await store.FindLatestAssessedAsync(user, programId, token);
        return latest?.SkillId is Guid skillId
            ? await PracticeAsync(user, skillId, token)
            : new LearningResult.NoActiveLesson();
    }

    public Task<LearningResult> ReviewAsync(UserId user, Guid skillId, CancellationToken token) =>
        StartSkillLessonAsync(user, skillId, SkillLessonKind.Review, token);

    public async Task<LearningResult> DeferReviewAsync(UserId user, Guid skillId, ReviewDeferral deferral, CancellationToken token)
    {
        ValidateUser(user, token);
        var schedule = await knowledge.ReviewAsync(user, skillId, token);
        if (schedule is null) return new LearningResult.SkillNotFound();
        var active = await store.FindActiveAsync(user, token);
        token.ThrowIfCancellationRequested();
        if (active?.SkillId == skillId) return new LearningResult.InvalidTransition();
        if (!knowledge.IsDue(schedule)) return new LearningResult.ReviewNotDue();
        knowledge.Defer(schedule, deferral);
        return new LearningResult.ReviewDeferred(schedule.DueAt);
    }

    private enum SkillLessonKind
    {
        Practice,
        Review
    }

    private async Task<LearningResult> StartSkillLessonAsync(UserId user, Guid skillId, SkillLessonKind kind, CancellationToken token,
        string? answeredQuestion = null)
    {
        ValidateUser(user, token);
        var status = await knowledge.StatusAsync(user, skillId, token);
        if (status is null) return new LearningResult.SkillNotFound();
        var skill = status.Skill;
        var active = await store.FindActiveAsync(user, token);
        var replacesRevealedChoice = kind == SkillLessonKind.Practice &&
            active is { AcceptsAnswer: true, Choice.SolutionSeen: true } && active.SkillId == skill.Id;
        if (active is not null && !replacesRevealedChoice) return Session(active);
        if (kind == SkillLessonKind.Review)
        {
            var schedule = await knowledge.ReviewAsync(user, skillId, token);
            if (schedule is null || !knowledge.IsDue(schedule)) return new LearningResult.ReviewNotDue();
        }
        try
        {
            var program = curriculum is null ? null : await curriculum.FindAsync(user, token);
            TeachingContext? teaching = null;
            if (kind == SkillLessonKind.Practice)
            {
                teaching = new(TeachingAction.Practice, "User selected practice of this existing skill.",
                    status.State, []);
            }
            var strategy = teaching is null ? PedagogicalStrategy.Review : AdaptiveTeaching.StrategyFor(teaching.Action);
            var personalization = await knowledge.PersonalizationAsync(user, token);
            var needsOpenCheck = replacesRevealedChoice || AdaptiveTeaching.NeedsOpenCheck(status.Evidence.LastOrDefault());
            var recent = status.State.EvidenceSummary is not null || needsOpenCheck
                ? await store.RecentQuestionsAsync(user, skill.Id, token) : [];
            if (replacesRevealedChoice) recent = [active!.Question, .. recent];
            if (answeredQuestion is not null && !recent.Contains(answeredQuestion, StringComparer.Ordinal))
                recent = [answeredQuestion, .. recent];
            var request = new LessonRequest(skill.Description, Program: program is null ? null : LessonProgramContext.From(program),
                IsReview: kind == SkillLessonKind.Review,
                Teaching: teaching, Personalization: personalization.ForGeneration())
            {
                PreviousQuestions = recent.Length == 0 ? null : recent,
                PracticeEvidenceKind = kind == SkillLessonKind.Practice && status.State.EvidenceSummary is { } summary
                    ? summary.Understanding ? ExerciseEvidenceKind.Application : ExerciseEvidenceKind.Understanding
                    : kind == SkillLessonKind.Practice && needsOpenCheck ? ExerciseEvidenceKind.Understanding
                    : null
            };
            var material = await teacher.GenerateAsync(ChoiceTeaching.SelectFormat(request, status.Evidence), token);
            token.ThrowIfCancellationRequested();
            var requiredKind = needsOpenCheck && request.IsReview ? ExerciseEvidenceKind.Retrieval : request.PracticeEvidenceKind;
            if ((needsOpenCheck || answeredQuestion is not null) && requiredKind is { } required &&
                !AdaptiveTeaching.IsValidFollowUp(material, recent, required) ||
                answeredQuestion is not null && AdaptiveTeaching.RepeatsQuestion(material.Question, recent))
                return new LearningResult.ProviderUnavailable();
            var lesson = await CreateWithChoiceHistoryAsync(user, skill.Title, material, token);
            if (lesson.Content?.StructuredRubric?.Version == ExerciseRubric.QuestionScopedVersion)
            {
                var fingerprint = RubricEvidence.FingerprintQuestion(lesson.Question);
                if (status.Evidence.Any(item => item.RubricEvidence?.QuestionFingerprint == fingerprint))
                    return new LearningResult.ProviderUnavailable();
            }
            lesson.SetTeaching(new(strategy, personalization.EffectivePreferences()));
            lesson.LinkToSkill(skill);
            if (program is not null) lesson.LinkPracticeToProgram(program);
            if (kind == SkillLessonKind.Review) lesson.MarkAsReview();
            if (replacesRevealedChoice) active!.Finish();
            store.Add(lesson);
            return Material(lesson);
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new LearningResult.ProviderUnavailable();
        }
    }

    public async Task<LearningResult> ResumeAsync(UserId user, CancellationToken token)
    {
        ValidateUser(user, token);
        var lesson = await store.FindActiveAsync(user, token);
        if (lesson is null) return new LearningResult.NoActiveLesson();
        if (lesson.Status == LessonStatus.Paused) lesson.Resume();
        return Session(lesson);
    }

    public async Task<LearningResult> FinishAsync(UserId user, CancellationToken token)
    {
        ValidateUser(user, token);
        var lesson = await store.FindActiveAsync(user, token);
        if (lesson is null) return new LearningResult.NoActiveLesson();
        lesson.Finish();
        return new LearningResult.Finished();
    }

    public Task<LearningResult> ClarifyAsync(UserId user, string request, CancellationToken token) =>
        ExplainAsync(user, request, LessonInteractionKind.Clarification, token);

    public Task<LearningResult> RevealAsync(UserId user, CancellationToken token) =>
        ExplainAsync(user, "Покажи полное решение текущего упражнения.", LessonInteractionKind.FullAnswer, token);

    public Task<LearningResult> ExplainAsync(UserId user, CancellationToken token) =>
        ExplainAsync(user, "Explain the current material differently.", LessonInteractionKind.AlternativeExplanation, token);

    public async Task<LearningResult> ExplainContextAsync(UserId user, string request, CancellationToken token)
    {
        ValidateUser(user, token);
        request = request.Trim();
        if (request.Length == 0) request = "Explain the recent material differently.";
        if (request.Length > 1000) return new LearningResult.InvalidClarification();
        var lesson = await store.FindActiveAsync(user, token);
        if (lesson is null && curriculum is not null && await curriculum.FindAsync(user, token) is { } program)
            lesson = await store.FindLatestAssessedAsync(user, program.Id, token);
        if (lesson is not { Status: LessonStatus.Paused or LessonStatus.Completed }) return new LearningResult.NoActiveLesson();
        try
        {
            var result = await teacher.ExplainAsync(Requests.LessonSnapshot.From(lesson) with
                { Question = "", Content = null, RubricEvaluation = null }, request, LessonInteractionKind.ConceptExplanation, token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result.Explanation) || result.Explanation.Length > 2500) return new LearningResult.ProviderUnavailable();
            if (lesson.Status == LessonStatus.Paused)
                lesson.AddExplanation(LessonInteractionKind.ConceptExplanation, request, result.Explanation, provenance: result.Provenance);
            return new LearningResult.ConceptReply(result.Explanation, result.Provenance);
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new LearningResult.ProviderUnavailable();
        }
    }

    private async Task<LearningResult> ExplainAsync(UserId user, string request, LessonInteractionKind kind, CancellationToken token)
    {
        ValidateUser(user, token);
        request = request.Trim();
        if (string.IsNullOrWhiteSpace(request) || request.Length > 1000) return new LearningResult.InvalidClarification();
        var lesson = await store.FindActiveAsync(user, token);
        if (lesson is null) return new LearningResult.NoActiveLesson();
        if (lesson.Status != LessonStatus.AwaitingAnswer || lesson.ExercisePending && kind == LessonInteractionKind.FullAnswer)
            return new LearningResult.InvalidTransition();
        if (lesson.AcceptsAnswer && lesson.Choice is not null) return await ExplainChoiceAsync(lesson, request, kind, token);
        if (kind == LessonInteractionKind.FullAnswer)
        {
            if (lesson.Interactions.FirstOrDefault(interaction => interaction.Kind == LessonInteractionKind.FullAnswer) is { } solution)
                return Explanation(lesson, solution.Explanation, solution.Provenance);
        }
        else if (!lesson.CanOfferMoreHelp)
        {
            if (IsHintRequest(request)) return new LearningResult.HelpLimit();
            kind = LessonInteractionKind.ConceptExplanation;
        }
        try
        {
            LearningPreferenceValues? preferences = null;
            string? factDiscipline = null;
            if (curriculum is not null)
            {
                var program = lesson.ProgramId is { } id ? await curriculum.FindAsync(user, id, token) : null;
                var profile = await knowledge.PersonalizationAsync(user, token, program?.Discipline);
                var effective = profile.EffectivePreferences();
                preferences = (ExplanationPreference.Parse(request)?.ForExplanation() ?? effective).WithFallback(effective);
                if (program is not null && MarketLearningContent.Identify(request, lesson.Topic + " " + program.Discipline) != MarketFactRequest.None)
                    factDiscipline = program.Discipline;
            }
            var preferenceSnapshot = preferences is { } values && (!values.IsEmpty() || lesson.Teaching?.Preferences.IsEmpty() == false)
                ? values : null;
            var snapshot = Requests.LessonSnapshot.From(lesson) with
                { ExplanationPreferences = preferenceSnapshot, FactCheckDiscipline = factDiscipline };
            var conceptOnly = lesson.ExercisePending || kind == LessonInteractionKind.ConceptExplanation;
            if (conceptOnly) snapshot = snapshot with { Question = "", Content = null, RubricEvaluation = null };
            var result = !conceptOnly && preferenceSnapshot is null && factDiscipline is null
                ? await teacher.ExplainAsync(lesson, request, kind, token)
                : await teacher.ExplainAsync(snapshot, request, kind, token);
            token.ThrowIfCancellationRequested();
            lesson.AddExplanation(kind, request, result.Explanation, preferenceSnapshot, result.Provenance);
            return lesson.ExercisePending
                ? Session(lesson) with { Theory = result.Explanation, Provenance = result.Provenance, LastExplanation = null, TheoryExpanded = true }
                : Explanation(lesson, result.Explanation, result.Provenance);
        }
        catch (Exception exception) when (exception is LessonProviderException or ArgumentException)
        {
            return new LearningResult.ProviderUnavailable();
        }
    }

    private static LearningResult.Explanation Explanation(Lesson lesson, string explanation, LessonProvenance? provenance) =>
        new(explanation, lesson.Topic, lesson.ResponseQuestion, provenance)
        {
            InRussian = MarketLearningContent.InRussian(lesson.Theory),
            TaskContent = lesson.ResponseQuestion == lesson.Question ? lesson.Content : null
        };

    internal static LearningResult.Material Material(Lesson lesson) => new(lesson.Theory, lesson.Question, lesson.Topic)
    {
        Provenance = lesson.Provenance, HasReferenceCheck = lesson.UsesReferenceCheck, ProjectActionId = lesson.ProjectActionId,
        Transfer = lesson.Transfer, Content = lesson.Content, Choice = ChoicePrompt.From(lesson),
        IncludeTheory = !lesson.TheorySkipped
    };

    public static LearningResult.Session Session(Lesson lesson) => new(lesson.Topic, lesson.ResponseQuestion, lesson.Status)
    {
        Provenance = lesson.Provenance, ProjectActionId = lesson.ProjectActionId, Transfer = lesson.Transfer,
        Content = lesson.Content, Theory = lesson.TheorySkipped ? null : lesson.Theory,
        IsClarifyingAnswer = lesson.ResponseQuestion != lesson.Question,
        LastExplanation = lesson.SavedGuidance, LastExplanationProvenance = lesson.SavedGuidanceProvenance, Choice = ChoicePrompt.From(lesson),
        PendingExerciseId = lesson.ExercisePending ? lesson.Id : null
    };

    private static void ValidateUser(UserId user, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!user.IsValid)
        {
            throw new ArgumentException("Invalid user.", nameof(user));
        }
    }
}
