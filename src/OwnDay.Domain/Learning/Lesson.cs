namespace OwnDay.Domain.Learning;

public sealed partial class Lesson
{
    public const int MaxHelpSteps = 2;
    private static readonly IAnswerCheck _answerCheck = new NormalizedTextAnswerCheck();
    private readonly List<LessonInteraction> _interactions = [];
    private Lesson() { }

    public Guid Id { get; private set; }
    public UserId UserId { get; private set; }
    public string Topic { get; private set; } = "";
    public string Theory { get; private set; } = "";
    public string Question { get; private set; } = "";
    public string? Answer { get; private set; }
    public string? Feedback { get; private set; }
    public bool? IsCorrect { get; private set; }
    public bool? AiIsCorrect { get; private set; }
    public bool? DeterministicIsCorrect { get; private set; }
    public AnswerAssessmentStatus AssessmentStatus { get; private set; }
    public string? ExpectedAnswer { get; private set; }
    public AnswerReferenceKind ReferenceKind { get; private set; }
    public LessonProvenance? Provenance { get; private set; }
    public LessonStatus Status { get; private set; }
    public bool ExercisePending { get; private set; }
    public bool TheorySkipped { get; private set; }
    public bool AcceptsAnswer => Status == LessonStatus.AwaitingAnswer && !ExercisePending;
    public LessonStatus? SuspendedFrom { get; private set; }
    public Guid? SuspendedForProgramId { get; private set; }
    public Guid? ProgramId { get; private set; }
    public Guid? PracticeProgramId { get; private set; }
    public int? RouteVersion { get; private set; }
    public string? TopicCode { get; private set; }
    public Guid? SkillId { get; private set; }
    public long? ProjectActionId { get; private set; }
    public bool IsReview { get; private set; }
    public LessonTeaching? Teaching { get; private set; }
    public AnswerAssistance Assistance { get; private set; }
    public LearningContent? Content { get; private set; }
    public LessonRubricEvaluation? RubricEvaluation { get; private set; }
    public LearningContent? AssessmentContent => Content is { Id: LearningContent.GeneratedExerciseId, StructuredRubric.Version: 1 } legacy
        ? legacy with
        {
            Version = "question-v1",
            Criterion = Question.Length <= 500 ? Question : "Assess the question as asked.",
            Rubric = "Assess only the requirements expressed in the question.",
            StructuredRubric = new(1, ExerciseEvidenceKind.Recognition,
                [new("question", Question.Length <= 200 ? Question : "Assess the question as asked.")])
        }
        : Content;
    public bool UsesReferenceCheck => ExpectedAnswer is not null && ReferenceKind == AnswerReferenceKind.ExactKey;
    public bool HasPendingLegacyAssessment => Content is { Id: LearningContent.GeneratedExerciseId, StructuredRubric.Version: 1 } &&
        RubricEvaluation is { Final: false, ScopedToQuestion: false };
    public string ResponseQuestion => !HasPendingLegacyAssessment &&
        RubricEvaluation is { Final: false, AwaitingCorrection: false, Clarification: { } clarification }
        ? clarification : Question;
    public int CorrectionCount => RubricEvaluation is { Corrections.IsDefault: false } evaluation ? evaluation.Corrections.Length : 0;
    public bool CanOfferMoreHelp => !ExercisePending && Choice is { } choice
        ? !choice.SolutionSeen && choice.HintsUsed < MaxHelpSteps
        : Content?.StructuredRubric is null || CorrectionCount + _interactions.Count < MaxHelpSteps;
    public string? SavedGuidance => SavedExplanation?.Explanation ?? PendingCorrection?.Proposal.Guidance;
    public LessonProvenance? SavedGuidanceProvenance => SavedExplanation?.Provenance;
    private ExerciseCorrection? PendingCorrection => !HasPendingLegacyAssessment &&
        RubricEvaluation is { Final: false, AwaitingCorrection: true } evaluation ? evaluation.Corrections[^1] : null;
    private LessonInteraction? SavedExplanation => _interactions.FirstOrDefault(interaction => interaction.Kind == LessonInteractionKind.FullAnswer)
        ?? (PendingCorrection is null ? _interactions.MaxBy(interaction => interaction.Id) : null);
    public InterdisciplinaryPractice? Transfer { get; private set; }
    public IReadOnlyCollection<LessonInteraction> Interactions => _interactions.AsReadOnly();

    public static Lesson Start(UserId user, string topic, string theory, string question,
        LessonProvenance? provenance = null, string? expectedAnswer = null, LearningContent? content = null,
        AnswerReferenceKind referenceKind = AnswerReferenceKind.ExactKey)
    {
        if (!user.IsValid) throw new ArgumentException("Invalid user.", nameof(user));
        Validate(topic, 500);
        Validate(theory, 2500);
        Validate(question, 1000);
        provenance?.Validate();
        if (expectedAnswer is not null) Validate(expectedAnswer, 500);
        if (!Enum.IsDefined(referenceKind)) throw new ArgumentOutOfRangeException(nameof(referenceKind));
        var lesson = new Lesson { Id = Guid.NewGuid(), UserId = user, Topic = topic, Theory = theory, Question = question,
            Provenance = provenance, ExpectedAnswer = expectedAnswer, ReferenceKind = referenceKind, Assistance = AnswerAssistance.Independent };
        if (content is not null) lesson.SetContent(content);
        return lesson;
    }

    public void SetContent(LearningContent content)
    {
        RequireAwaitingAnswer();
        if (Choice is not null) throw new InvalidOperationException("The published choice is immutable.");
        content.Validate();
        content.StructuredRubric?.ValidateQuestion(Question);
        if (Content is not null) throw new InvalidOperationException("Lesson content is already set.");
        if (UsesReferenceCheck && content.Kind == LearningContentKind.OpenWriting)
            throw new InvalidOperationException("Writing uses a rubric, not an exact answer key.");
        if (UsesReferenceCheck && content.StructuredRubric?.Version == ExerciseRubric.QuestionScopedVersion)
            throw new ArgumentException("This exercise uses a rubric and cannot require an exact answer key.", nameof(content));
        Content = content;
    }

    public void Complete(string answer, string feedback, bool correct)
    {
        if (Content?.StructuredRubric is not null || Choice is not null)
            throw new InvalidOperationException("A rubric assessment is required.");
        CommitAssessment(answer, feedback, correct);
    }

    public void ApplyRubricAssessment(string answer, string feedback, RubricAssessment assessment, Guid? operationId,
        ExerciseCorrectionProposal? correction = null)
    {
        RequireAwaitingAnswer();
        if (AssessmentContent?.StructuredRubric is not { } rubric)
            throw new InvalidOperationException("This exercise has no structured rubric.");
        Validate(answer, 3000);
        Validate(feedback, 3000);
        if (operationId == Guid.Empty) throw new ArgumentException("Invalid operation identity.", nameof(operationId));
        assessment.Validate(rubric);

        var prior = RubricEvaluation;
        var legacyAttempt = HasPendingLegacyAssessment;
        var scopedToQuestion = Content is { Id: LearningContent.GeneratedExerciseId, StructuredRubric.Version: 1 };
        var priorClarification = legacyAttempt ? null : prior?.Clarification;
        var evaluation = new LessonRubricEvaluation(assessment, prior?.InitialAnswer ?? answer, operationId,
            priorClarification, Final: false, prior?.InitialAssessment ?? prior?.Assessment,
            prior?.InitialOperationId ?? prior?.OperationId, prior?.Corrections ?? default,
            ClarificationAnswer: prior?.ClarificationAnswer, ScopedToQuestion: scopedToQuestion);

        var unclear = assessment.Criteria.FirstOrDefault(item => item.Result == RubricCriterionResult.Unclear);
        if (unclear is not null && priorClarification is null && !legacyAttempt)
        {
            var unclearCodes = assessment.Criteria.Where(item => item.Result == RubricCriterionResult.Unclear)
                .Select(item => item.Code).ToHashSet(StringComparer.Ordinal);
            var clarification = scopedToQuestion ? Question : string.Join('\n', rubric.Criteria
                .Where(criterion => unclearCodes.Contains(criterion.Code)).Select(criterion => criterion.Requirement));
            RubricEvaluation = evaluation with { Clarification = clarification, ClarificationAnswer = answer };
            return;
        }

        var unmet = assessment.Criteria.FirstOrDefault(item => item.Result == RubricCriterionResult.NotMet);
        if (unclear is null && unmet is not null && CanOfferMoreHelp)
        {
            var requirement = scopedToQuestion ? Question : rubric.Criteria.Single(criterion => criterion.Code == unmet.Code).Requirement;
            var proposal = correction is { IsInstructional: true } ? correction : new(ExerciseCorrectionKind.Hint, requirement);
            var attempt = new ExerciseCorrection(answer, assessment, operationId, proposal);
            RubricEvaluation = evaluation with
            {
                Corrections = evaluation.Corrections.IsDefault ? [attempt] : evaluation.Corrections.Add(attempt),
                AwaitingCorrection = true
            };
            Assistance = AnswerAssistance.Assisted;
            return;
        }

        var correct = assessment.Criteria.All(item => item.Result == RubricCriterionResult.Met);
        var usedClarification = legacyAttempt || prior is { AwaitingCorrection: false, Clarification: not null };
        RubricEvaluation = evaluation with
        {
            Final = true, FinalThroughClarification = usedClarification,
            ClarificationAnswer = legacyAttempt ? prior!.ClarificationAnswer ?? prior.InitialAnswer : evaluation.ClarificationAnswer
        };
        var referenceCandidate = prior is { AwaitingCorrection: false, Clarification: not null }
            ? prior.ClarificationAnswer ?? prior.InitialAnswer : answer;
        CommitAssessment(answer, feedback, correct, referenceCandidate);
        if (unclear is not null)
        {
            AssessmentStatus = AnswerAssessmentStatus.Inconclusive;
            AiIsCorrect = null;
            DeterministicIsCorrect = null;
            IsCorrect = false;
        }
    }

    private void CommitAssessment(string answer, string feedback, bool correct, string? referenceCandidate = null)
    {
        RequireAwaitingAnswer();
        Validate(answer, 3000);
        Validate(feedback, 3000);
        Answer = answer;
        Feedback = feedback;
        AiIsCorrect = correct;
        DeterministicIsCorrect = UsesReferenceCheck ? _answerCheck.Check(referenceCandidate ?? answer, ExpectedAnswer!) : null;
        AssessmentStatus = DeterministicIsCorrect is null ? AnswerAssessmentStatus.AiOnly
            : DeterministicIsCorrect == correct ? AnswerAssessmentStatus.Agreement : AnswerAssessmentStatus.Conflict;
        IsCorrect = correct && AssessmentStatus != AnswerAssessmentStatus.Conflict;
        Status = LessonStatus.Completed;
    }

    public void LinkToProgram(LearningProgram program, string topicCode)
    {
        RequireAwaitingAnswer();
        if (IsReview || ProgramId is not null || PracticeProgramId is not null || program.UserId != UserId || program.DiagnosticPending ||
            !program.CurrentRoute.Topics.Any(t => t.Code == topicCode))
            throw new ArgumentException("Invalid program topic.");
        ProgramId = program.Id;
        RouteVersion = program.CurrentRoute.Version;
        TopicCode = topicCode;
    }

    public void LinkPracticeToProgram(LearningProgram program)
    {
        RequireAwaitingAnswer();
        if (ProgramId is not null || PracticeProgramId is not null || program.UserId != UserId)
            throw new ArgumentException("Invalid practice program.", nameof(program));
        PracticeProgramId = program.Id;
    }

    public void LinkToProjectAction(LearningProgram program, OwnDay.Domain.Actions.Action action)
    {
        RequireAwaitingAnswer();
        if (ProjectActionId is not null || action.Id <= 0 || action.UserId != UserId ||
            action.ProjectId != program.ProjectId || program.Id != ProgramId ||
            program.UserId != UserId || program.ProjectId is null)
            throw new ArgumentException("Project practice requires a linked project program and task.");
        ProjectActionId = action.Id;
    }

    public void Pause()
    {
        if (Status != LessonStatus.AwaitingAnswer) throw new InvalidOperationException("Lesson is not active.");
        InvalidateChoiceButtons();
        Status = LessonStatus.Paused;
    }

    public void PresentTheoryFirst()
    {
        RequireAwaitingAnswer();
        if (ProgramId is null || TheorySkipped || RubricEvaluation is not null || Choice is { Attempts.IsEmpty: false })
            throw new InvalidOperationException("Only an unattempted program exercise can follow theory.");
        ExercisePending = true;
    }

    public void OpenExercise()
    {
        if (Status != LessonStatus.AwaitingAnswer || !ExercisePending)
            throw new InvalidOperationException("No exercise is pending.");
        ExercisePending = false;
    }

    public void SkipTheory()
    {
        RequireAwaitingAnswer();
        if (ProgramId is null) throw new InvalidOperationException("Only a program lesson can skip theory.");
        TheorySkipped = true;
    }

    public void SetTeaching(LessonTeaching teaching)
    {
        RequireAwaitingAnswer();
        if (Teaching is not null || !Enum.IsDefined(teaching.Strategy)) throw new ArgumentException("Invalid teaching snapshot.");
        foreach (var value in new[] { teaching.Preferences.Format, teaching.Preferences.Depth, teaching.Preferences.Pace, teaching.Preferences.Examples })
            if (value is not null) Validate(value, LearningPreference.MaxValueLength);
        Teaching = teaching;
    }

    public void LinkTransfer(LearningProgram target, LearningProgram source, string sourceCode, ProgramSkillLink link)
    {
        RequireAwaitingAnswer();
        if (Transfer is not null || SkillId is null || ProgramId != target.Id || target.UserId != UserId ||
            link.TopicCode != TopicCode || link.SkillId != SkillId)
            throw new ArgumentException("Transfer requires a confirmed shared identity across disciplines.");
        Transfer = InterdisciplinaryPractice.Create(target, source, sourceCode, link);
    }

    public void LinkToSkill(Skill skill)
    {
        RequireAwaitingAnswer();
        if (SkillId is not null || skill.UserId != UserId)
            throw new ArgumentException("Invalid skill.", nameof(skill));
        SkillId = skill.Id;
    }

    public void Resume()
    {
        if (Status != LessonStatus.Paused) throw new InvalidOperationException("Lesson is not paused.");
        Status = LessonStatus.AwaitingAnswer;
    }

    public void SuspendForProgramSwitch(LearningProgram current)
    {
        if (current.UserId != UserId || ProgramId is { } programId && programId != current.Id ||
            Status is not (LessonStatus.AwaitingAnswer or LessonStatus.Paused) || SuspendedForProgramId is not null)
            throw new InvalidOperationException("Only the current owned question can be suspended.");
        SuspendedFrom = Status;
        SuspendedForProgramId = current.Id;
        InvalidateChoiceButtons();
        Status = LessonStatus.Suspended;
    }

    public void RestoreForProgram(LearningProgram selected)
    {
        if (selected.UserId != UserId || SuspendedForProgramId != selected.Id || Status != LessonStatus.Suspended ||
            SuspendedFrom is not (LessonStatus.AwaitingAnswer or LessonStatus.Paused))
            throw new InvalidOperationException("The selected program does not own this return point.");
        Status = SuspendedFrom.Value;
        SuspendedFrom = null;
        SuspendedForProgramId = null;
    }

    public void MarkAsReview()
    {
        RequireAwaitingAnswer();
        if (SkillId is null || ProgramId is not null || IsReview)
            throw new InvalidOperationException("A review requires a linked skill outside route progression.");
        IsReview = true;
    }

    public void Finish()
    {
        if (Status is not (LessonStatus.AwaitingAnswer or LessonStatus.Paused))
            throw new InvalidOperationException("Lesson has ended.");
        Status = LessonStatus.Finished;
    }

    public void AddExplanation(LessonInteractionKind kind, string request, string explanation, LearningPreferenceValues? preferences = null,
        LessonProvenance? provenance = null)
    {
        if (Status != LessonStatus.AwaitingAnswer && (Status != LessonStatus.Paused || kind != LessonInteractionKind.ConceptExplanation) ||
            ExercisePending && kind == LessonInteractionKind.FullAnswer)
            throw new InvalidOperationException("This explanation is unavailable at the current lesson stage.");
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == LessonInteractionKind.FullAnswer
            ? _interactions.Any(interaction => interaction.Kind == LessonInteractionKind.FullAnswer)
            : kind != LessonInteractionKind.ConceptExplanation && !CanOfferMoreHelp)
            throw new InvalidOperationException("The exercise has reached its help limit.");
        Validate(request, 1000);
        Validate(explanation, 2500);
        provenance?.Validate();
        var choice = !ExercisePending && kind != LessonInteractionKind.FullAnswer && Choice is { } state
            ? state with
            {
                HintsUsed = kind == LessonInteractionKind.ConceptExplanation ? state.HintsUsed : state.HintsUsed + 1,
                Revision = checked(state.Revision + 1)
            }
            : Choice;
        _interactions.Add(new LessonInteraction(Id, kind, request, explanation, preferences, provenance));
        Choice = choice;
        if (!ExercisePending) Assistance = AnswerAssistance.Assisted;
    }

    public void ReplaceExerciseWith(Lesson replacement)
    {
        if (Status != LessonStatus.AwaitingAnswer || replacement.UserId != UserId || replacement.Topic != Topic ||
            replacement.Id == Id || !replacement.AcceptsAnswer || replacement.ProgramId is not null || replacement.PracticeProgramId is not null ||
            replacement.SkillId is not null || replacement.IsReview || replacement.Teaching is not null)
            throw new InvalidOperationException("A fresh exercise for the same owned topic is required.");
        replacement.ProgramId = ProgramId;
        replacement.PracticeProgramId = PracticeProgramId;
        replacement.RouteVersion = RouteVersion;
        replacement.TopicCode = TopicCode;
        replacement.SkillId = SkillId;
        replacement.ProjectActionId = ProjectActionId;
        replacement.Transfer = Transfer;
        replacement.IsReview = IsReview;
        replacement.Teaching = Teaching;
        Finish();
    }

    private void RequireAwaitingAnswer()
    {
        if (!AcceptsAnswer) throw new InvalidOperationException("Lesson is not awaiting an answer.");
    }

    private static void Validate(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException("Invalid lesson text.", nameof(value));
    }
}
