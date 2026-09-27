using System.Globalization;
using System.Resources;

namespace OwnDay.Infrastructure.Telegram.Localization;

public static class TelegramTexts
{
    private static readonly ResourceManager Resources = new(
        "OwnDay.Infrastructure.Telegram.Localization.TelegramMessages",
        typeof(TelegramTexts).Assembly);

    public static string ResolveLocale(string? telegramLanguageCode) =>
        telegramLanguageCode is not null &&
        (telegramLanguageCode.Equals("ru", StringComparison.OrdinalIgnoreCase) ||
         telegramLanguageCode.StartsWith("ru-", StringComparison.OrdinalIgnoreCase))
            ? "ru"
            : "en";

    public static string Get(string locale, string key, params object?[] arguments)
    {
        var culture = CultureInfo.GetCultureInfo(locale);
        var template = Resources.GetString(key, culture) ??
            throw new InvalidOperationException($"Telegram translation '{key}' is missing for '{locale}'.");
        template = template.Replace("\r\n", "\n", StringComparison.Ordinal);
        return arguments.Length == 0 ? template : string.Format(culture, template, arguments);
    }
}
