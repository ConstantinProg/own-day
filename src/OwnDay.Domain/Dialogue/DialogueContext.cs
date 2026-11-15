using System.Security.Cryptography;
using System.Text;
using System.Collections.Immutable;
using OwnDay.Domain.Learning;

namespace OwnDay.Domain.Dialogue;

public enum DialoguePhase { Consultation, DiagnosticOffer, Diagnostic, Learning, Paused, Confirmation }
public enum DialogueExpectedResponse { None, ConsultationDetail, DiagnosticChoice, DiagnosticAnswer, LessonAnswer, IntentChoice, ChangeConfirmation }
public enum DialogueOperationKind
{
    None, Answer, Clarify, Explain, Pause, Resume, Finish, Next, Progress,
    CreateProgram, CreateProjectProgram, SelectProgram, ChangeGoal, Cancel, Confirm, Diagnose,
    BeginPractice, NextPractice, PausePractice, ResumePractice, StopPractice, PracticeInfo, StartOtherLesson,
    SwitchProgram, SwitchGoal, SwitchCreateProgram, SwitchCreateProjectProgram
}

public sealed record DialogueClarification(Guid OriginalRequestId, ImmutableArray<DialogueOperationKind> Allowed,
    string TargetStamp, Guid PromptId, long BaseRevision, DateTimeOffset OpenedAt, DateTimeOffset ExpiresAt, long SourceOrder = 0);
public sealed record DialogueProposalTarget(Guid? ProgramId, int? RouteVersion, Guid? LessonId, string? QuestionFingerprint,
    uint? ProgramVersion = null, uint? LessonVersion = null);
public sealed record DialogueConfirmation(Guid ProposalId, string Code, Guid OriginalRequestId,
    DialogueOperationKind Operation, string Arguments, string ParametersHash, string TargetStamp,
    Guid PromptId, long BaseRevision, DateTimeOffset OpenedAt, DateTimeOffset ExpiresAt,
    DialogueProposalTarget? Target = null, string? Label = null, long SourceOrder = 0);

// Conversation expectations only. Lesson and LearningProgram remain authoritative
// for pedagogical state; there are no jobs, provider results or transport types here.
public sealed partial class DialogueContext
{
    public const int CurrentSchemaVersion = 5;
    private DialogueContext() { }
    public UserId UserId { get; private set; }
    public int SchemaVersion { get; private set; } = CurrentSchemaVersion;
    public long Revision { get; private set; }
    public DialoguePhase Phase { get; private set; }
    public DialogueExpectedResponse ExpectedResponse { get; private set; }
    public Guid PromptId { get; private set; }
    public Guid? TargetLessonId { get; private set; }
    public string? QuestionFingerprint { get; private set; }
    public DateTimeOffset? PromptOpenedAt { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }
    public DateTimeOffset? InterruptedAt { get; private set; }
    public long InterruptedSourceOrder { get; private set; }
    public long ResponseAfterSourceOrder { get; private set; }
    public Guid? DeferredGoalRequestId { get; private set; }
    public DialogueClarification? PendingClarification { get; private set; }
    public DialogueConfirmation? PendingConfirmation { get; private set; }
    public ConsultationDraft? Consultation { get; private set; }
    public DiagnosticPromptTarget? DiagnosticTarget { get; private set; }

    public static DialogueContext Create(UserId user, Lesson? lesson, DateTimeOffset now)
    {
        if (!user.IsValid) throw new ArgumentException("Invalid owner.", nameof(user));
        var context = new DialogueContext { UserId = user };
        context.Synchronize(lesson, now);
        // A legacy question already exists; do not invent its publication time.
        context.PromptOpenedAt = null;
        return context;
    }

    public void Synchronize(Lesson? lesson, DateTimeOffset now, long sourceOrder = 0)
    {
        if (lesson is not null && lesson.UserId != UserId) throw new ArgumentException("Foreign lesson.", nameof(lesson));
        if (lesson?.Status is not (LessonStatus.AwaitingAnswer or LessonStatus.Paused)) lesson = null;
        var fingerprint = lesson is null ? null : Hash(lesson.Question);
        var changed = TargetLessonId != lesson?.Id || QuestionFingerprint != fingerprint;
        var newExpectation = changed || ExpectedResponse != DialogueExpectedResponse.LessonAnswer;
        ClearPending(now);
        DiagnosticTarget = null;
        TargetLessonId = lesson?.Id;
        QuestionFingerprint = fingerprint;
        PromptId = lesson is null ? Guid.Empty : QuestionPrompt(lesson.Id, fingerprint!);
        if (changed) PromptOpenedAt = lesson is null ? null : now;
        Phase = lesson?.Status == LessonStatus.Paused ? DialoguePhase.Paused : DialoguePhase.Learning;
        ExpectedResponse = lesson?.Status == LessonStatus.AwaitingAnswer ? DialogueExpectedResponse.LessonAnswer : DialogueExpectedResponse.None;
        if (lesson is null) ResponseAfterSourceOrder = Math.Max(ResponseAfterSourceOrder, sourceOrder);
        else if (lesson.Status == LessonStatus.AwaitingAnswer && newExpectation && sourceOrder > 0)
            ResponseAfterSourceOrder = Math.Max(ResponseAfterSourceOrder, sourceOrder);
    }

    public void Interrupt(DateTimeOffset now, long sourceOrder = 0)
    {
        ClearPending(now);
        DiagnosticTarget = null;
        InterruptedAt = now;
        InterruptedSourceOrder = Math.Max(InterruptedSourceOrder, Math.Max(ResponseAfterSourceOrder, sourceOrder));
        PromptId = Guid.Empty;
        TargetLessonId = null;
        QuestionFingerprint = null;
        ExpectedResponse = DialogueExpectedResponse.None;
        Phase = DialoguePhase.Learning;
    }

    public bool MatchesQuestion(Lesson? lesson) => lesson is null ? TargetLessonId is null :
        lesson.UserId == UserId && TargetLessonId == lesson.Id && QuestionFingerprint == Hash(lesson.Question);

    public void AdvanceResponseBoundary(long sourceOrder)
    {
        if (SchemaVersion != CurrentSchemaVersion || sourceOrder < 0) throw new InvalidOperationException("Invalid dialogue source boundary.");
        ResponseAfterSourceOrder = Math.Max(ResponseAfterSourceOrder, sourceOrder);
    }

    public void OpenClarification(Guid originalRequestId, DialogueOperationKind[] allowed, string stamp, DateTimeOffset now, long sourceOrder = 0)
    {
        if (originalRequestId == Guid.Empty || allowed.Length is < 2 or > 3 || allowed.Distinct().Count() != allowed.Length ||
            allowed.Any(kind => !Enum.IsDefined(kind) || kind is DialogueOperationKind.None or DialogueOperationKind.Confirm))
            throw new ArgumentException("Invalid clarification.", nameof(allowed));
        ValidateStamp(stamp);
        ClearPending(now);
        PromptId = Guid.NewGuid();
        ExpectedResponse = DialogueExpectedResponse.IntentChoice;
        PendingClarification = new(originalRequestId, allowed.ToImmutableArray(), stamp, PromptId, Revision, now, now.AddMinutes(10), sourceOrder);
    }

    public Guid? ConsumeClarification(DialogueOperationKind choice, DateTimeOffset receivedAt, string stamp, DateTimeOffset now, long sourceOrder = 0)
    {
        var pending = PendingClarification;
        if (pending is null || ExpectedResponse != DialogueExpectedResponse.IntentChoice || pending.PromptId != PromptId ||
            pending.BaseRevision != Revision || receivedAt < pending.OpenedAt || now >= pending.ExpiresAt ||
            pending.TargetStamp != stamp || !pending.Allowed.Contains(choice) || pending.SourceOrder > 0 && sourceOrder <= pending.SourceOrder) return null;
        ClearPending(now);
        return pending.OriginalRequestId;
    }

    public void OpenConfirmation(Guid originalRequestId, DialogueOperationKind operation, string arguments, string stamp, DateTimeOffset now,
        DialogueProposalTarget? target = null, string? label = null, long sourceOrder = 0)
    {
        if (originalRequestId == Guid.Empty || !Enum.IsDefined(operation) || operation is not
            (DialogueOperationKind.Finish or DialogueOperationKind.SelectProgram or DialogueOperationKind.CreateProgram or
             DialogueOperationKind.CreateProjectProgram or DialogueOperationKind.ChangeGoal or DialogueOperationKind.SwitchProgram or
             DialogueOperationKind.SwitchGoal or DialogueOperationKind.SwitchCreateProgram or DialogueOperationKind.SwitchCreateProjectProgram) ||
            arguments.Length > 4000 || label?.Length > 500)
            throw new ArgumentException("Invalid consequential proposal.", nameof(operation));
        ValidateStamp(stamp);
        ClearPending(now);
        Phase = DialoguePhase.Confirmation;
        PromptId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        PendingConfirmation = new(proposalId, proposalId.ToString("N")[..8], originalRequestId, operation, arguments,
            Hash(operation + "\n" + arguments), stamp, PromptId, Revision, now, now.AddMinutes(10),
            target ?? new(null, null, TargetLessonId, QuestionFingerprint), label, sourceOrder);
        ExpectedResponse = DialogueExpectedResponse.ChangeConfirmation;
    }

    public DialogueConfirmation? ConsumeConfirmation(string code, DateTimeOffset receivedAt, string stamp, DateTimeOffset now, long sourceOrder = 0)
    {
        var pending = PendingConfirmation;
        if (pending is null || ExpectedResponse != DialogueExpectedResponse.ChangeConfirmation || pending.PromptId != PromptId ||
            pending.BaseRevision != Revision || receivedAt < pending.OpenedAt || now >= pending.ExpiresAt || pending.TargetStamp != stamp ||
            !string.Equals(code, pending.Code, StringComparison.OrdinalIgnoreCase) ||
            pending.ParametersHash != Hash(pending.Operation + "\n" + pending.Arguments) || pending.SourceOrder > 0 && sourceOrder <= pending.SourceOrder) return null;
        ClearPending(now);
        return pending;
    }

    public void DeferGoal(Guid originalRequestId, DateTimeOffset now)
    {
        if (originalRequestId == Guid.Empty) throw new ArgumentException("Missing source.", nameof(originalRequestId));
        ClearPending(now);
        DeferredGoalRequestId = originalRequestId;
    }

    private void ClearPending(DateTimeOffset now)
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new InvalidOperationException("Unsupported dialogue schema.");
        PendingClarification = null;
        PendingConfirmation = null;
        ExpectedResponse = DialogueExpectedResponse.None;
        Phase = Phase == DialoguePhase.Paused ? DialoguePhase.Paused : DialoguePhase.Learning;
        PromptId = TargetLessonId is { } lessonId && QuestionFingerprint is { } fingerprint ? QuestionPrompt(lessonId, fingerprint) : Guid.Empty;
        Revision = checked(Revision + 1);
        LastActivityAt = now;
    }

    private static void ValidateStamp(string stamp)
    {
        if (stamp.Length != 64) throw new ArgumentException("Missing target stamp.", nameof(stamp));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Guid QuestionPrompt(Guid lessonId, string fingerprint) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(lessonId.ToString("N") + ":" + fingerprint)).AsSpan(0, 16));
}
