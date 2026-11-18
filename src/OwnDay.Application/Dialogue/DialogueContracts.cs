using System.Text.Json;
using System.Text.Json.Serialization;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Dialogue;

[JsonConverter(typeof(JsonStringEnumConverter<LearningIntent>))]
public enum LearningIntent { Ambiguous, Answer, Clarify, Pause, Resume, Progress, Next, GoalChange, StudyGoal, RelevantExperience }
public sealed record LearningIntentInput(string Input, string Question, string? Goal, string? Discipline,
    string[] RecentInteractions, LearningIntent[] Allowed, Guid? ProgramId = null, int? RouteVersion = null,
    uint? ProgramVersion = null, long DialogueRevision = 0, Guid? PromptId = null);
public sealed record LearningIntentProposal(LearningIntent Intent)
{
    public static LearningIntentProposal Parse(string json)
    {
        if (json.Length > 300) throw new JsonException("Oversized intent proposal.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty("Intent", out var value) || value.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<LearningIntent>(value.GetString(), ignoreCase: false, out var intent) || !Enum.IsDefined(intent) ||
            intent.ToString() != value.GetString())
            throw new JsonException("Invalid intent proposal.");
        return new(intent);
    }
}
public interface ILearningIntentResolver
{
    Task<LearningIntentProposal> ResolveAsync(LearningIntentInput input, CancellationToken token);
}

public sealed record LearningTextInput(string Text, Guid? LessonId, Guid? PromptId, long? DialogueRevision, bool ConsultationAllowed = false,
    bool EnglishDiagnosticAllowed = false, bool PracticeAllowed = false, bool ProgramSwitchAllowed = false);
public sealed record DialogueProgramSnapshot(Guid Id, string Goal, string Discipline, int RouteVersion, uint Version = 0,
    bool HasEnglishDiagnostic = false, LearningPracticeStatus? PracticeStatus = null,
    Guid? SuspendedLessonId = null, uint? SuspendedLessonVersion = null, [property: JsonIgnore] string? PendingDiagnosticQuestion = null,
    [property: JsonIgnore] bool HasSubjectDiagnostic = false);
public sealed record DialogueOriginalInput(string Text, Guid? LessonId, Guid? PromptId);
public interface IDialogueStore
{
    Task<DialogueContext?> FindAsync(UserId user, CancellationToken token);
    void Add(DialogueContext context);
    Task<DialogueProgramSnapshot?> ReadProgramAsync(UserId user, CancellationToken token);
    Task<DialogueProgramSnapshot?> ReadProgramByIdAsync(UserId user, Guid programId, CancellationToken token);
    Task<DialogueOriginalInput?> ReadOriginalAsync(UserId user, Guid requestId, CancellationToken token);
}

public sealed record DialogueTurn(Guid RequestId, UserId UserId, DialogueOperationKind? Command, string Text,
    Guid? ReceiptLessonId, Guid? ReceiptPromptId, DateTimeOffset ReceivedAt, DateTimeOffset Now, uint? LessonVersion = null, long SourceOrder = 0);
public sealed record DialogueAudit(string Code, DialogueConfirmation? Confirmation = null, DialogueClarification? Clarification = null,
    Guid? OriginalRequestId = null, DialogueOperationKind? ChosenAction = null, int Version = 1);
public enum DialogueReplyKind
{
    Unavailable, Interrupted, Cancelled, ConfirmationRejected, ChoiceUnbound, ChoiceExpired, OriginalStale,
    NoActiveLesson, Paused, NeedBinding, NeedActualInput, Recap, MustFinishLesson, ProgramUnavailable,
    GoalDeferred, ChooseAnswerOrQuestion, ChooseQuestionOrGoal, ChoosePauseOrResume, ConfirmFinish,
    ConfirmProgramSelection, ConfirmProgramCreation, ConfirmGoalIntent,
    AskGoal, AskPracticalUse, AskExperience, ConsultationRetry, ConsultationUnavailable, FirstProgramReady, ConsultationPaused, MustEndPractice,
    AskNewGoal, ConfirmProgramSwitch, ConfirmGoalSwitch, GoalChanged, DiagnosticOffer, DiagnosticQuestion, AskRelevantExperience
}
public sealed record DialogueReply(DialogueReplyKind Kind, IReadOnlyList<string>? Values = null, bool InEnglish = false);
public sealed record DialogueDecision(DialogueContext Context, DialogueOperationKind? Execute, string Arguments,
    DialogueReply? Reply = null, DialogueAudit? Audit = null, FirstProgramCreation? Creation = null);
public sealed record FirstProgramCreation(string Goal, string Experience, string? GoalCriterion);
