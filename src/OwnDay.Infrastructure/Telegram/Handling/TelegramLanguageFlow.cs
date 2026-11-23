using System.Text.Json;
using OwnDay.Application.Settings;
using OwnDay.Domain;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Actions;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Localization;
using Telegram.Bot.Types.ReplyMarkups;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramLanguageFlow(EfUserLanguageStore store, UserLanguageSettings settings, TimeProvider clock,
    TelegramActionCodec codec)
{
    public async Task<(string Locale, bool IsExplicit)> ResolveAsync(UserId user, string? clientLanguage, CancellationToken token)
    {
        var selected = await settings.ReadAsync(user, token);
        return (selected ?? TelegramTexts.ResolveLocale(clientLanguage), selected is not null);
    }

    internal async Task<TelegramLearningReply> HandleAsync(UserId user, string command, string arguments,
        int updateId, string locale, bool isExplicit, CancellationToken token)
    {
        if (arguments.Length == 0)
        {
            var title = TelegramTexts.Get(locale, command == "settings" ? "settings.title" : "language.title");
            var language = TelegramTexts.Get(locale, "language.name." + locale);
            var source = TelegramTexts.Get(locale, isExplicit ? "language.source.explicit" : "language.source.automatic");
            var text = title + "\n" + TelegramTexts.Get(locale, "language.current", language, source) +
                "\n\n" + TelegramTexts.Get(locale, "language.choices");
            var row = await store.GetOrCreateAsync(user, token);
            var expiry = clock.GetUtcNow().AddMinutes(10);
            row.SelectionUpdateId = updateId;
            row.SelectionExpiresAt = expiry.UtcDateTime;
            InlineKeyboardButton[] buttons =
            [
                InlineKeyboardButton.WithCallbackData("Русский", codec.EncodeLanguage("ru", user.Value, updateId, expiry)),
                InlineKeyboardButton.WithCallbackData("English", codec.EncodeLanguage("en", user.Value, updateId, expiry))
            ];
            return new(text, JsonSerializer.Serialize(new InlineKeyboardMarkup(new[] { buttons })));
        }

        if (command == "language" && await settings.SetAsync(user, arguments, token) is { } selected)
        {
            await CloseAsync(user, token);
            return new(TelegramTexts.Get(selected, "language.changed"));
        }
        return new(TelegramTexts.Get(locale, command == "settings" ? "settings.usage" : "language.usage"));
    }

    internal async Task<TelegramLearningReply> SelectAsync(UserId user, string language, int menuUpdateId, DateTimeOffset expiresAt,
        string locale, CancellationToken token)
    {
        var row = await store.FindAsync(user, token);
        if (row?.SelectionUpdateId != menuUpdateId || expiresAt <= clock.GetUtcNow())
            return new(TelegramTexts.Get(locale, "language.expired"));

        await settings.SetAsync(user, language, token);
        row.SelectionExpiresAt = null;
        row.SelectionUpdateId = null;
        return new(TelegramTexts.Get(language, "language.changed"));
    }

    public async Task CloseAsync(UserId user, CancellationToken token)
    {
        if (await store.FindAsync(user, token) is { SelectionExpiresAt: not null } row)
        {
            row.SelectionExpiresAt = null;
            row.SelectionUpdateId = null;
        }
    }
}
