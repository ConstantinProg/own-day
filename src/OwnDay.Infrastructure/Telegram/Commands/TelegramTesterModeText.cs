using OwnDay.Application.TesterMode;

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
        (TesterModeResult.Active, true) => "Режим тестировщика активен. /tester_off отключает его.",
        (TesterModeResult.Active, false) => "Tester Mode is active. Use /tester_off to turn it off.",
        (TesterModeResult.Revoked, true) => "Доступ к режиму тестировщика отключён.",
        (TesterModeResult.Revoked, false) => "Tester Mode access is off.",
        (TesterModeResult.Cancelled, true) => "Действие в режиме тестировщика отменено.",
        (TesterModeResult.Cancelled, false) => "Tester Mode action cancelled.",
        (TesterModeResult.Expired, true) => "Время ожидания истекло. Если хотите продолжить, запустите команду заново.",
        (TesterModeResult.Expired, false) => "The request expired. Run the command again to continue.",
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };
}
