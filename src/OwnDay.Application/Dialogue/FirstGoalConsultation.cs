using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using OwnDay.Application.Learning;

namespace OwnDay.Application.Dialogue;

public static class FirstGoalConsultation
{
    public static DialogueDecision CompleteCreation(DialogueContext context, CurriculumResult result, DialogueTurn turn)
    {
        if (result is CurriculumResult.Program created)
        {
            context.CompleteConsultation(created.Value, turn.Now, turn.SourceOrder);
            return new(context, null, "", new(DialogueReplyKind.FirstProgramReady,
                [created.Value.Goal, context.Consultation!.PracticalUse!]), new("first_program_created"));
        }
        context.RestoreConsultation(turn.Now, turn.SourceOrder);
        return new(context, null, "", new(DialogueReplyKind.ConsultationRetry), new("consultation_creation_failed"));
    }

    public static DialogueDecision Decide(DialogueContext context, DialogueTurn turn)
    {
        var text = turn.Text.Trim();
        var normalized = text.ToLowerInvariant().TrimEnd('.', '!');
        if (context.Consultation is null)
        {
            if (text.Length is < 5 or > LearningProgram.MaxGoalLength ||
                !(normalized.StartsWith("хочу ", StringComparison.Ordinal) || normalized.StartsWith("научиться ", StringComparison.Ordinal) ||
                  normalized.StartsWith("i want to ", StringComparison.Ordinal) || normalized.StartsWith("learn ", StringComparison.Ordinal)))
                return Reply(context, DialogueReplyKind.AskGoal);
            context.BeginConsultation(turn.RequestId, text, turn.Now, turn.SourceOrder);
            return Reply(context, DialogueReplyKind.AskPracticalUse);
        }

        if (context.Consultation.Version != 1) return Reply(context, DialogueReplyKind.Unavailable);
        if (context.Consultation.CreatedProgramId is not null) return Reply(context, DialogueReplyKind.ConsultationUnavailable);
        if (normalized is "пауза" or "pause" or "отмена" or "cancel" or "потом")
        {
            context.Interrupt(turn.Now, turn.SourceOrder);
            return Reply(context, DialogueReplyKind.ConsultationPaused);
        }
        if (normalized is "продолжим" or "продолжить" or "resume" ||
            context.ExpectedResponse != DialogueExpectedResponse.ConsultationDetail)
        {
            context.RestoreConsultation(turn.Now, turn.SourceOrder);
            return Prompt(context);
        }
        if (context.Consultation.AfterSourceOrder > 0 && turn.SourceOrder <= context.Consultation.AfterSourceOrder ||
            turn.ReceiptPromptId != context.PromptId) return Prompt(context);

        if (context.Consultation.Experience is not null)
            return normalized is "повторить" or "создать" or "retry" ? Create(context) : Prompt(context);
        if (normalized is "да" or "нет" or "yes" or "no" or "ответ" or "вопрос" or "answer" or "question" ||
            text.Length < 3 || text.EndsWith('?') || context.Consultation.PracticalUse is null && normalized == "не знаю") return Prompt(context);
        if (normalized.StartsWith("хочу ", StringComparison.Ordinal)) return Prompt(context);
        if (context.Consultation.PracticalUse is null)
        {
            if (text.Length > 400) return Prompt(context);
            context.RecordPracticalUse(text, turn.Now, turn.SourceOrder);
            return Reply(context, DialogueReplyKind.AskExperience);
        }
        if (text.Length > 500) return Prompt(context);
        context.RecordExperience(text, turn.Now, turn.SourceOrder);
        return Create(context);
    }

    public static DialogueDecision Prompt(DialogueContext context) => Reply(context,
        context.Consultation?.PracticalUse is null ? DialogueReplyKind.AskPracticalUse :
            context.Consultation.Experience is null ? DialogueReplyKind.AskExperience : DialogueReplyKind.ConsultationRetry);

    private static DialogueDecision Create(DialogueContext context)
    {
        var draft = context.Consultation!;
        return new(context, null, "", Creation: new(draft.OriginalGoal,
            "Применение: " + draft.PracticalUse + "\nОпыт: " + draft.Experience));
    }
    private static DialogueDecision Reply(DialogueContext context, DialogueReplyKind kind) => new(context, null, "", new(kind));
}
