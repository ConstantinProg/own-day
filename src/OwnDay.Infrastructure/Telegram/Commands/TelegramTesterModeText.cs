using OwnDay.Application.TesterMode;
using OwnDay.Domain.Dialogue;

namespace OwnDay.Infrastructure.Telegram.Commands;

internal static class TelegramTesterModeText
{
    public static string Render(TesterModeResult result, bool russian) => (result, russian) switch
    {
        (TesterModeResult.Disabled, true) => "Режим тестировщика отключён.",
        (TesterModeResult.Disabled, false) => "Tester Mode is disabled.",
        (TesterModeResult.AuthorizationRequired, true) => "Для доступа используйте /tester и отправьте код отдельным сообщением.",
        (TesterModeResult.AuthorizationRequired, false) => "Use /tester and send the code in a separate message.",
        (TesterModeResult.PromptForSecret, true) => "Отправьте секретный код отдельным сообщением. Для отмены используйте /cancel.",
        (TesterModeResult.PromptForSecret, false) => "Send the secret code in a separate message. Use /cancel to stop.",
        (TesterModeResult.InvalidSecret, true) => "Неверный код. Попробуйте ещё раз или используйте /cancel.",
        (TesterModeResult.InvalidSecret, false) => "Incorrect code. Try again or use /cancel.",
        (TesterModeResult.Locked, true) => "Слишком много попыток. Повторите /tester позже.",
        (TesterModeResult.Locked, false) => "Too many attempts. Try /tester again later.",
        (TesterModeResult.Active, true) => "Режим тестировщика активен.\n/tester_status — моё состояние.\n/export, /export recent, /export last — диагностика.\n/tester_reset — сбросить мои данные.\n/tester_delete_all — удалить все мои данные.\n/tester_off — отключить доступ.",
        (TesterModeResult.Active, false) => "Tester Mode is active.\n/tester_status — view my state.\n/export, /export recent, /export last — diagnostics.\n/tester_reset — reset my data.\n/tester_delete_all — delete all my data.\n/tester_off — turn off access.",
        (TesterModeResult.Revoked, true) => "Доступ к режиму тестировщика отключён.",
        (TesterModeResult.Revoked, false) => "Tester Mode access is off.",
        (TesterModeResult.Cancelled, true) => "Действие в режиме тестировщика отменено.",
        (TesterModeResult.Cancelled, false) => "Tester Mode action cancelled.",
        (TesterModeResult.Expired, true) => "Время ожидания истекло. Если хотите продолжить, запустите команду заново.",
        (TesterModeResult.Expired, false) => "The request expired. Run the command again to continue.",
        (TesterModeResult.ConfirmReset, true) => "Сброс удалит ваши задачи, обучение, настройки и диалоги. Доступ тестировщика сохранится. Подтвердите сброс кнопкой или введите /cancel.",
        (TesterModeResult.ConfirmReset, false) => "Reset clears your tasks, learning, settings, and dialogue. Tester Mode access stays active. Confirm with the button or use /cancel.",
        (TesterModeResult.Reset, true) => "Ваше состояние сброшено. Отправьте /start, чтобы начать заново.",
        (TesterModeResult.Reset, false) => "Your state was reset. Send /start to begin again.",
        (TesterModeResult.ConfirmDelete, true) => "Ваши пользовательские данные и история будут удалены из рабочей базы OwnDay. Доступ тестировщика сохранится. Нажмите «Продолжить удаление моих данных» или введите /cancel.",
        (TesterModeResult.ConfirmDelete, false) => "Your user data and history will be deleted from OwnDay's working database. Tester Mode access stays active. Choose Continue deleting my data or use /cancel.",
        (TesterModeResult.PromptForDeletePhrase, true) => "Для удаления отправьте DELETE заглавными латинскими буквами отдельным сообщением. Для отмены введите /cancel.",
        (TesterModeResult.PromptForDeletePhrase, false) => "To delete your data, send DELETE in uppercase as a separate message. Use /cancel to stop.",
        (TesterModeResult.InvalidDeletePhrase, true) => "Контрольная фраза не совпала. Удаление отменено. Начните заново с /tester_delete_all.",
        (TesterModeResult.InvalidDeletePhrase, false) => "The phrase did not match. Deletion was cancelled. Run /tester_delete_all to start again.",
        (TesterModeResult.Deleted, true) => "Ваши пользовательские данные удалены из рабочей базы OwnDay. Отправьте /start, чтобы начать заново.",
        (TesterModeResult.Deleted, false) => "Your user data was deleted from OwnDay's working database. Send /start to begin again.",
        (TesterModeResult.InvalidCommand, true) => "Введите /tester для списка команд. Команды не принимают аргументы.",
        (TesterModeResult.InvalidCommand, false) => "Use /tester for the list of commands. Send commands without arguments.",
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    public static string ConfirmationLabel(TesterModeOperation operation, bool russian) => (operation, russian) switch
    {
        (TesterModeOperation.Reset, true) => "Сбросить мои данные",
        (TesterModeOperation.Reset, false) => "Reset my data",
        (TesterModeOperation.DeleteAll, true) => "Продолжить удаление моих данных",
        (TesterModeOperation.DeleteAll, false) => "Continue deleting my data",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public static string RenderStatus(TesterModeStatus status, bool russian)
    {
        var progress = status.CompletedTopics is { } completed
            ? (russian ? $"Темы выбранной программы — {completed} из {status.TotalTopics} завершено."
                : $"Selected program — {completed} of {status.TotalTopics} topics completed.")
            : (russian ? "Нет выбранной программы." : "No program selected.");
        var dialogue = DescribeDialogue(status.Phase, status.ExpectedResponse, russian);
        return russian
            ? $"Задачи — открыто {status.ActiveTasks}, завершено {status.CompletedTasks}.\nПрограммы — {status.Programs}. {progress}\n" +
              $"Упражнения — активно {status.ActiveExercises}, на паузе {status.PausedExercises}, отложено {status.SuspendedExercises}.\n" +
              $"Диалог — {dialogue}.\nУчебные запросы в обработке или очереди — {status.PendingLearningRequests}."
            : $"Tasks — {status.ActiveTasks} open, {status.CompletedTasks} completed.\nPrograms — {status.Programs}. {progress}\n" +
              $"Exercises — {status.ActiveExercises} active, {status.PausedExercises} paused, {status.SuspendedExercises} suspended.\n" +
              $"Dialogue — {dialogue}.\nLearning requests queued or processing — {status.PendingLearningRequests}.";
    }

    private static string DescribeDialogue(DialoguePhase? phase, DialogueExpectedResponse? expectedResponse, bool russian)
    {
        var (ru, en) = phase switch
        {
            DialoguePhase.Consultation => ("выбор учебной цели", "choosing a learning goal"),
            DialoguePhase.DiagnosticOffer => ("предложена диагностика", "diagnostic offered"),
            DialoguePhase.Diagnostic => ("диагностика", "diagnostic in progress"),
            DialoguePhase.Learning => ("обучение", "learning"),
            DialoguePhase.Paused => ("пауза", "paused"),
            DialoguePhase.Confirmation => ("подтверждение", "confirmation"),
            _ => ("нет активного диалога", "no active dialogue")
        };
        var (expectedRu, expectedEn) = expectedResponse switch
        {
            DialogueExpectedResponse.ConsultationDetail => (", ожидаются уточнения цели", ", waiting for goal details"),
            DialogueExpectedResponse.DiagnosticChoice => (", ожидается выбор", ", waiting for a choice"),
            DialogueExpectedResponse.DiagnosticAnswer or DialogueExpectedResponse.LessonAnswer => (", ожидается ответ", ", waiting for an answer"),
            DialogueExpectedResponse.IntentChoice => (", ожидается уточнение", ", waiting for clarification"),
            DialogueExpectedResponse.ChangeConfirmation => (", ожидается подтверждение", ", waiting for confirmation"),
            _ => (string.Empty, string.Empty)
        };
        return russian ? ru + expectedRu : en + expectedEn;
    }
}
