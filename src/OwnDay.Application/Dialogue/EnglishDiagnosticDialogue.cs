using OwnDay.Application.Learning;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Dialogue;

public sealed record EnglishDiagnosticTurnResult(CurriculumResult Result, string Outcome);

public sealed class EnglishDiagnosticDialogue(ICurriculumStore programs, CurriculumService curriculum, ILearningIntentResolver resolver)
{
    public async Task<EnglishDiagnosticTurnResult> HandleAsync(DialogueContext context, DialogueTurn turn, string input, CancellationToken token)
    {
        var program = await programs.FindAsync(turn.UserId, token);
        if (program?.EnglishDiagnostic is not { Supported: true } diagnostic)
            return new(new CurriculumResult.InvalidTransition(), "diagnostic_unsupported");
        var normalized = input.Trim().ToLowerInvariant().TrimEnd('.', '!');
        if (!program.DiagnosticPending)
        {
            context.Synchronize(null, turn.Now, turn.SourceOrder);
            return normalized is "start" or "resume" or "продолжим" or "продолжить"
                ? new(await curriculum.NextAsync(turn.UserId, token), "diagnostic_already_finished")
                : new(new CurriculumResult.Diagnostic(program), "diagnostic_already_finished");
        }
        if (normalized is "прогресс" or "progress")
            return new(await curriculum.ProgressAsync(turn.UserId, 1, token), "diagnostic_progress");
        if (normalized is "ответ" or "вопрос" or "answer" or "question") return Choice(program);
        if (normalized.StartsWith("хочу ", StringComparison.Ordinal) || normalized.StartsWith("сменить ", StringComparison.Ordinal) ||
            normalized.Contains("ignore previous", StringComparison.Ordinal) || normalized.Contains("ignore system", StringComparison.Ordinal) ||
            normalized is "другая цель" or "другая тема" or "да" or "давай" or "нет" or "yes" or "no" && diagnostic.Status != EnglishDiagnosticStatus.Offered)
            return Choice(program);
        if (normalized is "пауза" or "pause" or "отмена" or "cancel" or "потом")
        {
            context.Interrupt(turn.Now, turn.SourceOrder);
            return new(new CurriculumResult.Diagnostic(program, "Остановимся здесь. Ответы сохранены. Напишите «Продолжим»."), "diagnostic_paused");
        }
        if (normalized is "продолжим" or "продолжить" or "resume")
        {
            if (program.DiagnosticPending)
            {
                context.BindDiagnostic(program, turn.Now, turn.SourceOrder);
                return new(new CurriculumResult.Diagnostic(program), "diagnostic_resumed");
            }
            return new(await curriculum.NextAsync(turn.UserId, token), "diagnostic_already_finished");
        }
        var bound = context.MatchesDiagnostic(program) && turn.ReceiptPromptId == context.PromptId &&
            turn.SourceOrder > context.ResponseAfterSourceOrder &&
            (context.PromptOpenedAt is null || turn.ReceivedAt >= context.PromptOpenedAt);
        if (!bound)
        {
            context.BindDiagnostic(program, turn.Now, turn.SourceOrder);
            return new(new CurriculumResult.Diagnostic(program, "Прочитайте текущее задание и ответьте после него; ничего не оценено."), "diagnostic_unbound");
        }
        if (normalized == "start" && diagnostic.Status == EnglishDiagnosticStatus.Started)
            return new(new CurriculumResult.Diagnostic(program), "diagnostic_current_item");
        var action = input;
        if (normalized is "пропустим" or "пропустить" or "skip") action = "skip";
        else if (diagnostic.Status == EnglishDiagnosticStatus.Offered)
        {
            if (normalized is "начнём диагностику" or "начнем диагностику" or "давай" or "да" or "yes" or "start") action = "start";
            else return new(new CurriculumResult.Diagnostic(program), "diagnostic_offer_repeated");
        }
        else if (normalized.StartsWith("объясни", StringComparison.Ordinal) || normalized.StartsWith("почему ", StringComparison.Ordinal) ||
            normalized is "помощь" or "не понял" or "не знаю" or "help") action = "help";
        else if (diagnostic.Results.Length == 1 && turn.Command != DialogueOperationKind.Diagnose &&
            !normalized.StartsWith("ответ:", StringComparison.Ordinal))
        {
            LearningIntentProposal proposal;
            try
            {
                proposal = await resolver.ResolveAsync(new(input, EnglishLearningContent.WritingQuestion, program.Goal, program.Discipline,
                    [], [LearningIntent.Answer, LearningIntent.Clarify, LearningIntent.Ambiguous], program.Id,
                    program.CurrentRoute.Version, DialogueRevision: context.Revision, PromptId: context.PromptId), token);
            }
            catch (LessonProviderException)
            {
                return Choice(program);
            }
            if (proposal.Intent == LearningIntent.Clarify) action = "help";
            else if (proposal.Intent != LearningIntent.Answer) return Choice(program);
        }
        var result = await curriculum.DiagnoseAsync(turn.UserId, action, token);
        if (result is CurriculumResult.Diagnostic) context.BindDiagnostic(program, turn.Now, turn.SourceOrder);
        return new(result, result is CurriculumResult.ProviderUnavailable ? "diagnostic_provider_unavailable" : "diagnostic_reply");
    }

    private static EnglishDiagnosticTurnResult Choice(LearningProgram program) => new(new CurriculumResult.Diagnostic(program,
        "Неясно, это ответ или вопрос. Для оценки повторите исходный текст с «ответ:», для объяснения напишите «помощь». Ничего не оценено."), "diagnostic_intent_unclear");
}
