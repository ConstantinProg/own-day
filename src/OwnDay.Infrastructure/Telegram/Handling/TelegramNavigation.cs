using System.Text.Json;
using OwnDay.Infrastructure.Telegram.Localization;
using Telegram.Bot.Types.ReplyMarkups;

namespace OwnDay.Infrastructure.Telegram.Handling;

public static class TelegramNavigation
{
    public static string? Keyboard(string command, string locale)
    {
        string[] destinations = command switch
        {
            "list" => ["tasks", "projects", "ideas", "notes", "waiting", "inbox"],
            "start" or "help" => ["list", "inbox", "settings"],
            _ => []
        };
        return destinations.Length == 0 ? null : JsonSerializer.Serialize(new InlineKeyboardMarkup(
            destinations.Select(name => new[] { InlineKeyboardButton.WithCallbackData(
                TelegramTexts.Get(locale, "nav." + name), "nav:" + name) })));
    }
}
