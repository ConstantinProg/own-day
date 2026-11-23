using OwnDay.Domain;

namespace OwnDay.Application.Settings;

public interface IUserLanguageStore
{
    Task<string?> ReadAsync(UserId user, CancellationToken token);
    Task SetAsync(UserId user, string language, CancellationToken token);
}

public sealed class UserLanguageSettings(IUserLanguageStore store)
{
    public async Task<string?> ReadAsync(UserId user, CancellationToken token) =>
        Normalize(await store.ReadAsync(user, token));

    public async Task<string?> SetAsync(UserId user, string argument, CancellationToken token)
    {
        var language = Normalize(argument);
        if (language is not null && language != await ReadAsync(user, token))
            await store.SetAsync(user, language, token);
        return language;
    }

    private static string? Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "en" => "en",
        "ru" => "ru",
        _ => null
    };
}
