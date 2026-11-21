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
            if (created.Value.EnglishDiagnostic is not null) context.BindDiagnostic(created.Value, turn.Now, turn.SourceOrder);
            var draft = context.Consultation!;
            return new(context, null, "", new(DialogueReplyKind.FirstProgramReady,
                [created.Value.Goal, draft.PracticalUse!,
                    string.Join(" → ", created.Value.CurrentRoute.Topics.Take(3).Select(topic => topic.Title)),
                    created.Value.DiagnosticPending ? "diagnostic" : "start", created.Value.GoalCriterion ?? ""],
                InEnglish: InEnglish(created.Value.Goal)), new("first_program_created"));
        }
        if (result is CurriculumResult.ClarifyPracticalUse)
        {
            context.ClarifyPracticalUse(turn.Now, turn.SourceOrder);
            return Reply(context, DialogueReplyKind.ClarifyPracticalUse) with { Audit = new("practical_use_clarification") };
        }
        context.RestoreConsultation(turn.Now, turn.SourceOrder);
        return Reply(context, DialogueReplyKind.ConsultationRetry) with { Audit = new("consultation_creation_failed") };
    }

    public static async Task<DialogueDecision> DecideAsync(DialogueContext context, DialogueTurn turn,
        ILearningIntentResolver resolver, CancellationToken token)
    {
        var text = turn.Text.Trim();
        var normalized = text.ToLowerInvariant().TrimEnd('.', '!');
        if (context.Consultation is null)
        {
            if (text.Length is < 5 or > LearningProgram.MaxGoalLength ||
                normalized is "продолжим" or "продолжить" or "resume" or "continue" or "помощь" or "help" or "прогресс" or "progress" ||
                !IsExplicitStudyGoal(normalized) && !await MatchesConsultationIntentAsync(context, turn, resolver,
                    LearningIntent.StudyGoal, "Чему вы хотите научиться?", token))
                return new(context, null, "", new(DialogueReplyKind.AskGoal, InEnglish: InEnglish(text)));
            context.BeginConsultation(turn.RequestId, text, turn.Now, turn.SourceOrder, practicalUseDefinesCriterion: true);
            return Reply(context, DialogueReplyKind.AskPracticalUse);
        }

        if (context.Consultation.Version != 1) return Reply(context, DialogueReplyKind.Unavailable);
        if (context.Consultation.CreatedProgramId is not null) return Reply(context, DialogueReplyKind.ConsultationUnavailable);
        if (normalized is "пауза" or "pause" or "отмена" or "cancel" or "потом" or "later")
        {
            context.Interrupt(turn.Now, turn.SourceOrder);
            return Reply(context, DialogueReplyKind.ConsultationPaused);
        }
        if (normalized is "продолжим" or "продолжить" or "resume" or "continue" ||
            context.ExpectedResponse != DialogueExpectedResponse.ConsultationDetail)
        {
            context.RestoreConsultation(turn.Now, turn.SourceOrder);
            return Prompt(context);
        }
        if (context.Consultation.AfterSourceOrder > 0 && !turn.IsAfterSourceOrder(context.Consultation.AfterSourceOrder) ||
            turn.ReceiptPromptId != context.PromptId) return Prompt(context);

        if (context.Consultation is { PracticalUse: not null, Experience: not null })
            return normalized is "повторить" or "создать" or "retry" ? Create(context) : Prompt(context);
        if (normalized is "да" or "нет" or "yes" or "no" or "ответ" or "вопрос" or "answer" or "question" ||
            text.Length < 2 || text.EndsWith('?') || context.Consultation.PracticalUse is null && normalized == "не знаю") return Prompt(context);
        if (normalized.StartsWith("хочу ", StringComparison.Ordinal) &&
            (context.Consultation.PracticalUse is not null || !normalized.Contains(" чтобы ", StringComparison.Ordinal)))
            return Prompt(context);
        if (context.Consultation.PracticalUse is null)
        {
            if (text.Length > 400) return Prompt(context);
            if (normalized is "не уверен" or "не уверена" or "не могу сказать" or "not sure" or "i don't know")
                return Reply(context, DialogueReplyKind.ClarifyPracticalUse);
            context.RecordPracticalUse(text, turn.Now, turn.SourceOrder);
            return context.Consultation.Experience is null ? Reply(context, DialogueReplyKind.AskExperience) : Create(context);
        }
        if (text.Length > 500) return Prompt(context);
        var shortExperience = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2;
        if (context.Consultation.PracticalUseDefinesCriterion && shortExperience &&
            !LearnerExperience.IsBeginner(normalized) && normalized != "не знаю" &&
            !(EnglishLearningContent.IsEnglishGoal(context.Consultation.OriginalGoal) && normalized is "a1" or "a2" or "b1" or "b2" or "c1" or "c2") &&
            !await MatchesConsultationIntentAsync(context, turn, resolver, LearningIntent.RelevantExperience,
                "Что вы уже умеете в этой области?", token))
            return new(context, null, "", new(DialogueReplyKind.AskRelevantExperience,
                [context.Consultation.OriginalGoal], InEnglish(context.Consultation.OriginalGoal)));
        context.RecordExperience(text, turn.Now, turn.SourceOrder);
        return Create(context);
    }

    public static DialogueDecision Prompt(DialogueContext context) => Reply(context,
        context.Consultation?.PracticalUse is null ? DialogueReplyKind.AskPracticalUse :
            context.Consultation.Experience is null ? DialogueReplyKind.AskExperience : DialogueReplyKind.ConsultationRetry);

    private static DialogueDecision Create(DialogueContext context)
    {
        var draft = context.Consultation!;
        var english = InEnglish(draft.OriginalGoal);
        string? criterion = null;
        if (draft.PracticalUseDefinesCriterion)
            criterion = english
                ? $"Independently solve a new task in this situation and explain the solution: {draft.PracticalUse}"
                : $"Самостоятельно справиться с новой задачей в ситуации «{draft.PracticalUse}» и объяснить своё решение.";
        var experience = english && draft.PracticalUseDefinesCriterion
            ? $"Application: {draft.PracticalUse}\nExperience: {draft.Experience}"
            : $"Применение: {draft.PracticalUse}\nОпыт: {draft.Experience}";
        return new(context, null, "", Creation: new(draft.OriginalGoal, experience, criterion, draft.PracticalUse!));
    }
    private static DialogueDecision Reply(DialogueContext context, DialogueReplyKind kind) =>
        new(context, null, "", new(kind, InEnglish: InEnglish(context.Consultation?.OriginalGoal)));

    public static bool InEnglish(string? text) => text is not null &&
        text.Any(char.IsAsciiLetter) && !text.Any(character => character is >= '\u0400' and <= '\u052f');

    internal static bool IsExplicitStudyGoal(string text) =>
        EnglishLearningContent.IsEnglishGoal(text) ||
        text.StartsWith("научиться ", StringComparison.Ordinal) ||
        text.StartsWith("learn ", StringComparison.Ordinal) ||
        text.StartsWith("i want to learn ", StringComparison.Ordinal) ||
        text.StartsWith("хочу ", StringComparison.Ordinal) &&
        text[5..].Split(' ', 2)[0].TrimEnd(',', ':') is "научиться" or "изучать" or "изучить" or "понять" or "читать" or "писать" or "разобраться";

    private static async Task<bool> MatchesConsultationIntentAsync(DialogueContext context, DialogueTurn turn,
        ILearningIntentResolver resolver, LearningIntent expected, string question, CancellationToken token)
    {
        try
        {
            var proposal = await resolver.ResolveAsync(new(turn.Text, question, context.Consultation?.OriginalGoal, null,
                [], [expected, LearningIntent.Ambiguous], DialogueRevision: context.Revision, PromptId: context.PromptId), token);
            return proposal.Intent == expected;
        }
        catch (LessonProviderException)
        {
            return false;
        }
    }
}
