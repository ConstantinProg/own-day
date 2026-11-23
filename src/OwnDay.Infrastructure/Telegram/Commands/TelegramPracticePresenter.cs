using OwnDay.Application.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramPracticePresenter
{
    public static IReadOnlyList<TelegramLearningReply> Render(LearningPracticeResult result, bool compact = false, bool? inRussian = null)
    {
        var practice = result.Practice;
        var text = inRussian == false ? result.Kind switch
        {
            PracticeReplyKind.NeedProgram => "Create or choose a learning program first.",
            PracticeReplyKind.NeedDiagnosis => "Finish or skip the diagnostic first. Your results will be saved.",
            PracticeReplyKind.Current => $"Exercise {practice!.LessonIds.Length} of {practice.RequestedCount}.",
            PracticeReplyKind.ReadyNext => $"Completed {practice!.LessonIds.Length} of {practice.RequestedCount}. Write “next” or use the buttons below.",
            PracticeReplyKind.Paused => "Practice is paused. Your exercises and answers are saved. Write “continue” to resume.",
            PracticeReplyKind.Stopped => "Practice ended. Your answers are saved. You can return to the unfinished exercise separately.",
            PracticeReplyKind.Completed => $"Practice complete. You finished {practice!.LessonIds.Length} of {practice.RequestedCount}. Write “continue” to return to your program.",
            PracticeReplyKind.ProviderUnavailable => "Your practice plan is saved. The next exercise is unavailable. Write “next” to try again.",
            _ => "I couldn't start practice. Choose one to three exercises or write “help” to return to your current question."
        } : result.Kind switch
        {
            PracticeReplyKind.NeedProgram => "Сначала создайте или выберите учебную программу.",
            PracticeReplyKind.NeedDiagnosis => "Сначала завершите или пропустите диагностику. Результаты сохранятся.",
            PracticeReplyKind.Current => $"Упражнение {practice!.LessonIds.Length} из {practice.RequestedCount}.",
            PracticeReplyKind.ReadyNext => $"Выполнено {practice!.LessonIds.Length} из {practice.RequestedCount}. Напишите «дальше», «пауза» или «хватит практики».",
            PracticeReplyKind.Paused => "Практика на паузе. Упражнения и ответы сохранены. Напишите «Продолжим».",
            PracticeReplyKind.Stopped => "Практика остановлена. Ответы сохранены; незавершённый вопрос можно продолжить отдельно. Для новой практики: «давай два упражнения».",
            PracticeReplyKind.Completed => $"Практика завершена. Выполнено {practice!.LessonIds.Length} из {practice.RequestedCount}. Напишите «Продолжим», чтобы вернуться к программе.",
            PracticeReplyKind.ProviderUnavailable => "План практики сохранён, но новое упражнение пока недоступно. Для явной повторной попытки напишите «дальше».",
            _ => "Не удалось начать практику. Выберите от одного до трёх упражнений или напишите «помощь», чтобы вернуться к текущему вопросу."
        };
        var reply = TelegramLearningReply.WithActions(text, LearningActionProvider.ForPractice(result), inRussian: inRussian ?? true);
        return result.Lesson is null ? [reply] : [.. compact ? [] : new[] { reply }, .. TelegramLearningPresenter.Render(result.Lesson, inRussian)];
    }
}
