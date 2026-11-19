using OwnDay.Application.Learning;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Dialogue;

public static class LearningGoalChange
{
    public static DialogueConfirmation OpenUpdate(DialogueContext context, DialogueTurn turn, DialogueProgramSnapshot program,
        Lesson? lesson, string stamp, CurriculumResult.GoalUpdateOffered proposal)
    {
        context.OpenConfirmation(turn.RequestId, DialogueOperationKind.ApplyGoalUpdate, proposal.Snapshot, stamp, turn.Now,
            new(program.Id, program.RouteVersion, lesson?.Id, context.QuestionFingerprint, program.Version, turn.LessonVersion),
            proposal.Goal.Description, turn.SourceOrder);
        return context.PendingConfirmation!;
    }

    public static DialogueReply Complete(DialogueContext context, CurriculumResult result, DialogueTurn turn)
    {
        context.Synchronize(null, turn.Now, turn.SourceOrder);
        if (result is not CurriculumResult.Program created) return new(DialogueReplyKind.Unavailable);
        if (created.Value.DiagnosticPending && created.Value.EnglishDiagnostic is not null)
            context.BindDiagnostic(created.Value, turn.Now, turn.SourceOrder);
        return new(DialogueReplyKind.GoalChanged, [created.Value.Goal]);
    }
}
