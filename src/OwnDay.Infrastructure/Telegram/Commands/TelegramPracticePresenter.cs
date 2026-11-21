using OwnDay.Application.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramPracticePresenter
{
    public static IReadOnlyList<TelegramLearningReply> Render(LearningPracticeResult result, bool compact = false)
    {
        var practice = result.Practice;
        var text = result.Kind switch
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
        var reply = TelegramLearningReply.WithActions(text, LearningActionProvider.ForPractice(result), inRussian: true);
        return result.Lesson is null ? [reply] : [.. compact ? [] : new[] { reply }, .. TelegramLearningPresenter.Render(result.Lesson)];
    }
}
