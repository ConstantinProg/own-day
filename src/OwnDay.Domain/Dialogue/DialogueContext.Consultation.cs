using OwnDay.Domain.Learning;
using System.Text.Json.Serialization;

namespace OwnDay.Domain.Dialogue;

public sealed record ConsultationDraft(Guid OriginalRequestId, string OriginalGoal, string? PracticalUse = null,
    string? Experience = null, Guid? CreatedProgramId = null, long AfterSourceOrder = 0, int Version = 1,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool PracticalUseDefinesCriterion = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayGoal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayPracticalUse = null);

public sealed partial class DialogueContext
{
    public void BeginConsultation(Guid requestId, string goal, DateTimeOffset now, long sourceOrder,
        bool practicalUseDefinesCriterion = false)
    {
        if (requestId == Guid.Empty || string.IsNullOrWhiteSpace(goal) || goal.Length > LearningProgram.MaxGoalLength ||
            TargetLessonId is not null || Consultation is not null)
            throw new InvalidOperationException("Cannot start this consultation.");
        Consultation = new(requestId, goal, AfterSourceOrder: sourceOrder, PracticalUseDefinesCriterion: practicalUseDefinesCriterion);
        RestoreConsultation(now, sourceOrder);
    }

    public void RecordPracticalUse(string value, DateTimeOffset now, long sourceOrder)
    {
        ValidateConsultationDetail(value, 400);
        if (Consultation!.PracticalUse is not null) throw new InvalidOperationException("Practical use is already recorded.");
        Consultation = Consultation with { PracticalUse = value };
        RestoreConsultation(now, sourceOrder);
    }

    public void RecordExperience(string value, DateTimeOffset now, long sourceOrder)
    {
        ValidateConsultationDetail(value, 500);
        if (Consultation!.PracticalUse is null || Consultation.Experience is not null)
            throw new InvalidOperationException("Experience is not expected.");
        Consultation = Consultation with { Experience = value };
        RestoreConsultation(now, sourceOrder);
    }

    public void RestoreConsultation(DateTimeOffset now, long sourceOrder)
    {
        if (Consultation is not { Version: 1, CreatedProgramId: null } draft || TargetLessonId is not null)
            throw new InvalidOperationException("No resumable consultation.");
        ClearPending(now);
        Consultation = draft with { AfterSourceOrder = Math.Max(draft.AfterSourceOrder, sourceOrder) };
        Phase = DialoguePhase.Consultation;
        ExpectedResponse = DialogueExpectedResponse.ConsultationDetail;
        PromptId = Guid.NewGuid();
        PromptOpenedAt = now;
    }

    public void ClarifyPracticalUse(DateTimeOffset now, long sourceOrder)
    {
        if (Consultation is not { Version: 1, CreatedProgramId: null, PracticalUse: not null } draft || TargetLessonId is not null)
            throw new InvalidOperationException("No practical use to clarify.");
        Consultation = draft with { PracticalUse = null };
        RestoreConsultation(now, sourceOrder);
    }

    public void CompleteConsultation(LearningProgram program, DateTimeOffset now, long sourceOrder,
        string? displayGoal = null, string? displayPracticalUse = null)
    {
        if (Consultation is not { Version: 1, CreatedProgramId: null, PracticalUse: not null, Experience: not null } draft ||
            program.UserId != UserId || program.ProjectId is not null || program.Goal != draft.OriginalGoal)
            throw new InvalidOperationException("Program does not match this consultation.");
        if (displayGoal is not null) LearningProgram.Validate(displayGoal, LearningProgram.MaxGoalLength);
        if (displayPracticalUse is not null) LearningProgram.Validate(displayPracticalUse, 400);
        Consultation = draft with { CreatedProgramId = program.Id, AfterSourceOrder = Math.Max(draft.AfterSourceOrder, sourceOrder),
            DisplayGoal = displayGoal, DisplayPracticalUse = displayPracticalUse };
        RestoreDiagnosticOffer(program.Id, now, sourceOrder);
    }

    public void RestoreDiagnosticOffer(Guid programId, DateTimeOffset now, long sourceOrder)
    {
        if (Consultation is not { Version: 1 } draft || draft.CreatedProgramId != programId || TargetLessonId is not null)
            throw new InvalidOperationException("No diagnostic offer for this program.");
        ClearPending(now);
        Consultation = draft with { AfterSourceOrder = Math.Max(draft.AfterSourceOrder, sourceOrder) };
        Phase = DialoguePhase.DiagnosticOffer;
        ExpectedResponse = DialogueExpectedResponse.DiagnosticChoice;
        PromptId = Guid.NewGuid();
        PromptOpenedAt = now;
    }

    private void ValidateConsultationDetail(string value, int maxLength)
    {
        if (Consultation is not { Version: 1, CreatedProgramId: null } || TargetLessonId is not null ||
            string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new InvalidOperationException("Invalid consultation detail.");
    }
}
