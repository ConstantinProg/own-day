using Microsoft.EntityFrameworkCore;
using OwnDay.Domain;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Localization;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramLanguageFlow(OwnDayDbContext db, TimeProvider clock)
{
    private static readonly TimeSpan SelectionLifetime = TimeSpan.FromMinutes(10);

    public Task<bool> HasSelectionAsync(UserId user, CancellationToken token) =>
        db.TelegramUserLanguages.AnyAsync(row => row.UserId == user.Value && row.SelectionExpiresAt != null, token);

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
        var row = await GetOrCreateAsync(user, token);
        row.SelectionExpiresAt = clock.GetUtcNow().UtcDateTime.Add(SelectionLifetime);
        return [TelegramTexts.Get(locale, "language.menu")];
    }

    public async Task<IReadOnlyList<string>> SelectAsync(UserId user, string argument, string locale, CancellationToken token)
    {
        if (argument is not "en" and not "ru")
        {
            return await OpenAsync(user, locale, token);
        }
        var row = await GetOrCreateAsync(user, token);
        row.Locale = argument;
        row.SelectionExpiresAt = null;
        return [TelegramTexts.Get(argument, "language.changed")];
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
            if (int.TryParse(text.Trim(), out _))
            {
                return [TelegramTexts.Get(locale, "language.menu")];
            }
            row.SelectionExpiresAt = null;
            return null;
        }

        row.Locale = selected;
        row.SelectionExpiresAt = null;
        return [TelegramTexts.Get(selected, "language.changed")];
    }

    public async Task<bool> CancelAsync(UserId user, CancellationToken token)
    {
        var row = await db.TelegramUserLanguages.SingleOrDefaultAsync(value => value.UserId == user.Value, token);
        if (row?.SelectionExpiresAt is null)
        {
            return false;
        }

        var active = row.SelectionExpiresAt > clock.GetUtcNow().UtcDateTime;
        row.SelectionExpiresAt = null;
        return active;
    }

    public async Task CloseAsync(UserId user, CancellationToken token)
    {
        var row = await db.TelegramUserLanguages.SingleOrDefaultAsync(value => value.UserId == user.Value, token);
        if (row is not null)
        {
            row.SelectionExpiresAt = null;
        }
    }

    private async Task<TelegramUserLanguage> GetOrCreateAsync(UserId user, CancellationToken token)
    {
        var row = await db.TelegramUserLanguages.SingleOrDefaultAsync(value => value.UserId == user.Value, token);
        if (row is null)
        {
            row = new TelegramUserLanguage { UserId = user.Value };
            db.TelegramUserLanguages.Add(row);
        }
        return row;
    }
}
