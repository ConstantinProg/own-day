using Microsoft.EntityFrameworkCore;
using OwnDay.Domain;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Localization;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramLanguageFlow(OwnDayDbContext db, TimeProvider clock)
{
    private static readonly TimeSpan SelectionLifetime = TimeSpan.FromMinutes(10);

    public async Task<string> ResolveLocaleAsync(UserId user, string? telegramLanguageCode, CancellationToken token)
    {
        var selected = await db.TelegramUserLanguages
            .Where(row => row.UserId == user.Value)
            .Select(row => row.Locale)
            .SingleOrDefaultAsync(token);
        return selected ?? TelegramTexts.ResolveLocale(telegramLanguageCode);
    }

    public async Task<IReadOnlyList<string>> OpenAsync(UserId user, string locale, CancellationToken token)
    {
        var row = await db.TelegramUserLanguages.SingleOrDefaultAsync(value => value.UserId == user.Value, token);
        if (row is null)
        {
            row = new TelegramUserLanguage { UserId = user.Value };
            db.TelegramUserLanguages.Add(row);
        }

        row.SelectionExpiresAt = clock.GetUtcNow().UtcDateTime.Add(SelectionLifetime);
        return [TelegramTexts.Get(locale, "language.menu")];
    }

    public async Task<IReadOnlyList<string>?> ContinueAsync(UserId user, string text, string locale, CancellationToken token)
    {
        var row = await db.TelegramUserLanguages.SingleOrDefaultAsync(value => value.UserId == user.Value, token);
        if (row?.SelectionExpiresAt is null)
        {
            return null;
        }

        if (row.SelectionExpiresAt <= clock.GetUtcNow().UtcDateTime)
        {
            row.SelectionExpiresAt = null;
            return null;
        }

        var selected = text.Trim() switch
        {
            "1" => "en",
            "2" => "ru",
            _ => null
        };
        if (selected is null)
        {
            return [TelegramTexts.Get(locale, "language.menu")];
        }

        row.Locale = selected;
        row.SelectionExpiresAt = null;
        return [TelegramTexts.Get(selected, "language.changed")];
    }

    public async Task<bool> CancelAsync(UserId user, CancellationToken token)
    {
        var row = await db.TelegramUserLanguages.SingleOrDefaultAsync(value => value.UserId == user.Value, token);
        if (row?.SelectionExpiresAt is null || row.SelectionExpiresAt <= clock.GetUtcNow().UtcDateTime)
        {
            return false;
        }

        row.SelectionExpiresAt = null;
        return true;
    }

    public async Task CloseAsync(UserId user, CancellationToken token)
    {
        var row = await db.TelegramUserLanguages.SingleOrDefaultAsync(value => value.UserId == user.Value, token);
        if (row is not null)
        {
            row.SelectionExpiresAt = null;
        }
    }
}
