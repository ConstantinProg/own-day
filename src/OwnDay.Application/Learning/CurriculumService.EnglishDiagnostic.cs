using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public sealed partial class CurriculumService
{
    private async Task<CurriculumResult> DiagnoseEnglishAsync(LearningProgram program, string input, CancellationToken token)
    {
        var diagnostic = program.EnglishDiagnostic!;
        if (!diagnostic.Supported) return new CurriculumResult.InvalidTransition();
        if (await lessons.FindActiveAsync(program.UserId, token) is not null) return new CurriculumResult.LessonInProgress();
        if (!program.DiagnosticPending) return await NextAsync(program.UserId, token);
        var normalized = input.Trim().ToLowerInvariant().TrimEnd('.', '!');
        if (normalized is "skip" or "пропустим" or "пропустить")
        {
            program.SkipDiagnostic();
            var next = await NextAsync(program.UserId, token);
            return next is CurriculumResult.Lesson lesson ? lesson with
            {
                Introduction = "Диагностика пропущена. Уже записанные ответы сохранены; уровень не подтверждён. Начнём с короткого упражнения."
            } : next;
        }
        if (diagnostic.Status == EnglishDiagnosticStatus.Offered)
        {
            if (normalized is "start" or "начнём диагностику" or "начнем диагностику" or "да" or "yes") program.StartEnglishDiagnostic();
            return new CurriculumResult.Diagnostic(program);
        }
        if (normalized is "" or "продолжим" or "resume") return new CurriculumResult.Diagnostic(program);
        var index = diagnostic.Results.Length;
        if (normalized == "help")
        {
            program.MarkDiagnosticHelp();
            return new CurriculumResult.Diagnostic(program, EnglishLearningContent.Help(index));
        }
        var answer = input.Trim();
        if (answer.StartsWith("ответ:", StringComparison.OrdinalIgnoreCase)) answer = answer[6..].Trim();
        if (answer.Length is < 1 or > LearningProgram.MaxDiagnosticAnswerLength || index == 0 && !EnglishLearningContent.IsReadingChoice(answer))
            return new CurriculumResult.Diagnostic(program);
        LessonAssessment assessment;
        if (index == 0)
        {
            var correct = EnglishLearningContent.ReadingIsCorrect(answer);
            assessment = new(correct ? "Просьба найдена верно." : "Sam просит отправить записи встречи сегодня.", correct)
                { Source = "OwnDay authored reading key/work-email-v1" };
        }
        else
        {
            try
            {
                assessment = await teacher.AssessAsync(new LessonSnapshot(program.Id, program.UserId, "English diagnostic writing",
                    EnglishLearningContent.WritingText, EnglishLearningContent.WritingQuestion, LessonStatus.AwaitingAnswer, null, null,
                    EnglishLearningContent.Writing), answer, token);
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(assessment.Feedback) || assessment.Feedback.Length > 3000 ||
                    string.IsNullOrWhiteSpace(assessment.Source) || assessment.Source.Length > 200) return new CurriculumResult.ProviderUnavailable();
            }
            catch (LessonProviderException) { return new CurriculumResult.ProviderUnavailable(); }
        }
        program.RecordDiagnosticAnswer(EnglishLearningContent.Item(index), answer, assessment.Feedback, assessment.IsCorrect, assessment.Source);
        if (program.EnglishDiagnostic!.Results.Length < EnglishDiagnostic.ItemCount)
            return new CurriculumResult.Diagnostic(program, assessment.Feedback + " Это предварительное наблюдение, не оценка уровня.");
        var foundation = program.EnglishDiagnostic.Results.Any(result => !result.AssessedCorrect || result.Assistance != AnswerAssistance.Independent);
        program.CompleteEnglishDiagnostic(EnglishLearningContent.Topics(foundation));
        var first = await NextAsync(program.UserId, token);
        return first is CurriculumResult.Lesson started ? started with
        {
            Introduction = "Два задания завершены. Письмо: предварительная оценка AI, не подтверждение уровня.\n" + assessment.Feedback +
                (foundation ? "\nНачнём с опорой; ответы с помощью учитываются отдельно." : "\nПерейдём к короткому упражнению.")
        } : first;
    }
}
