namespace OwnDay.Domain.Learning;

public sealed class Lesson
{
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
    public LessonProvenance? Provenance { get; private set; }
    public LessonStatus Status { get; private set; }
    public LessonStatus? SuspendedFrom { get; private set; }
    public Guid? SuspendedForProgramId { get; private set; }
    public Guid? ProgramId { get; private set; }
    public int? RouteVersion { get; private set; }
    public string? TopicCode { get; private set; }
    public Guid? SkillId { get; private set; }
    public long? ProjectActionId { get; private set; }
    public bool IsReview { get; private set; }
    public LessonTeaching? Teaching { get; private set; }
    public AnswerAssistance Assistance { get; private set; }
    public LearningContent? Content { get; private set; }
    public LessonRubricEvaluation? RubricEvaluation { get; private set; }
    public string ResponseQuestion => RubricEvaluation is { Final: false, Clarification: { } clarification } ? clarification : Question;
    public InterdisciplinaryPractice? Transfer { get; private set; }
    public IReadOnlyCollection<LessonInteraction> Interactions => _interactions.AsReadOnly();

    public static Lesson Start(UserId user, string topic, string theory, string question,
        LessonProvenance? provenance = null, string? expectedAnswer = null, LearningContent? content = null)
    {
        if (!user.IsValid) throw new ArgumentException("Invalid user.", nameof(user));
        Validate(topic, 500);
        Validate(theory, 2500);
        Validate(question, 1000);
        provenance?.Validate();
        if (expectedAnswer is not null) Validate(expectedAnswer, 500);
        var lesson = new Lesson { Id = Guid.NewGuid(), UserId = user, Topic = topic, Theory = theory, Question = question,
            Provenance = provenance, ExpectedAnswer = expectedAnswer, Assistance = AnswerAssistance.Independent };
        if (content is not null) lesson.SetContent(content);
        return lesson;
    }

    public void SetContent(LearningContent content)
    {
        RequireAwaitingAnswer();
        content.Validate();
        if (Content is not null || content.Kind == LearningContentKind.OpenWriting && ExpectedAnswer is not null)
            throw new InvalidOperationException("Writing uses a rubric, not an exact answer key.");
        Content = content;
    }

    public void Complete(string answer, string feedback, bool correct)
    {
        if (Content?.StructuredRubric is not null)
            throw new InvalidOperationException("A rubric assessment is required.");
        CommitAssessment(answer, feedback, correct);
    }

    public void ApplyRubricAssessment(string answer, string feedback, RubricAssessment assessment, Guid? operationId)
    {
        RequireAwaitingAnswer();
        if (Content?.StructuredRubric is not { } rubric)
            throw new InvalidOperationException("This exercise has no structured rubric.");
        Validate(answer, 3000);
        Validate(feedback, 3000);
        if (operationId == Guid.Empty) throw new ArgumentException("Invalid operation identity.", nameof(operationId));
        assessment.Validate(rubric);

        var unclear = assessment.Criteria.FirstOrDefault(item => item.Result == RubricCriterionResult.Unclear);
        if (unclear is not null && RubricEvaluation is null)
        {
            var clarification = rubric.Criteria.Single(criterion => criterion.Code == unclear.Code).Requirement;
            RubricEvaluation = new(assessment, answer, operationId, clarification, Final: false);
            return;
        }

        RubricEvaluation = new(assessment, RubricEvaluation?.InitialAnswer ?? answer, operationId,
            RubricEvaluation?.Clarification, Final: true, RubricEvaluation?.Assessment, RubricEvaluation?.OperationId);
        var correct = assessment.Criteria.All(item => item.Result == RubricCriterionResult.Met);
        CommitAssessment(answer, feedback, correct, RubricEvaluation.InitialAnswer);
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
        DeterministicIsCorrect = ExpectedAnswer is null ? null : _answerCheck.Check(referenceCandidate ?? answer, ExpectedAnswer);
        AssessmentStatus = DeterministicIsCorrect is null ? AnswerAssessmentStatus.AiOnly
            : DeterministicIsCorrect == correct ? AnswerAssessmentStatus.Agreement : AnswerAssessmentStatus.Conflict;
        IsCorrect = correct && AssessmentStatus != AnswerAssessmentStatus.Conflict;
        Status = LessonStatus.Completed;
    }

    public void LinkToProgram(LearningProgram program, string topicCode)
    {
        RequireAwaitingAnswer();
        if (IsReview || ProgramId is not null || program.UserId != UserId || program.DiagnosticPending ||
            !program.CurrentRoute.Topics.Any(t => t.Code == topicCode))
            throw new ArgumentException("Invalid program topic.");
        ProgramId = program.Id;
        RouteVersion = program.CurrentRoute.Version;
        TopicCode = topicCode;
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
        RequireAwaitingAnswer();
        Status = LessonStatus.Paused;
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

    public void AddExplanation(LessonInteractionKind kind, string request, string explanation)
    {
        RequireAwaitingAnswer();
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Validate(request, 1000);
        Validate(explanation, 2500);
        _interactions.Add(new LessonInteraction(Id, kind, request, explanation));
        Assistance = AnswerAssistance.Assisted;
    }

    private void RequireAwaitingAnswer()
    {
        if (Status != LessonStatus.AwaitingAnswer) throw new InvalidOperationException("Lesson is not awaiting an answer.");
    }

    private static void Validate(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException("Invalid lesson text.", nameof(value));
    }
}
