using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Settings;
using OwnDay.Domain;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfUserLanguageStore(OwnDayDbContext db) : IUserLanguageStore
{
    public async Task<string?> ReadAsync(UserId user, CancellationToken token) =>
        (await FindAsync(user, token))?.Locale;

    public async Task SetAsync(UserId user, string language, CancellationToken token) =>
        (await GetOrCreateAsync(user, token)).Locale = language;

    internal async Task<TelegramUserLanguage?> FindAsync(UserId user, CancellationToken token) =>
        await db.TelegramUserLanguages.FindAsync([user.Value], token);

    internal async Task<TelegramUserLanguage> GetOrCreateAsync(UserId user, CancellationToken token)
    {
        var row = await FindAsync(user, token);
        if (row is not null) return row;
        row = new TelegramUserLanguage { UserId = user.Value };
        db.TelegramUserLanguages.Add(row);
        return row;
    }
}
