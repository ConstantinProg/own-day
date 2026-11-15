using OwnDay.Application.Learning;
using OwnDay.Domain.Dialogue;

namespace OwnDay.Application.Dialogue;

public static class LearningGoalChange
{
    public static DialogueReply Complete(DialogueContext context, CurriculumResult result, DialogueTurn turn)
    {
        context.Synchronize(null, turn.Now, turn.SourceOrder);
        if (result is not CurriculumResult.Program created) return new(DialogueReplyKind.Unavailable);
        if (created.Value.DiagnosticPending && created.Value.EnglishDiagnostic is not null)
            context.BindDiagnostic(created.Value, turn.Now, turn.SourceOrder);
        return new(DialogueReplyKind.GoalChanged, [created.Value.Goal]);
    }
}
