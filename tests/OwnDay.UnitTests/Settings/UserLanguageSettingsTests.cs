using OwnDay.Application.Settings;
using OwnDay.Domain;
using Xunit;

namespace OwnDay.UnitTests.Settings;

public sealed class UserLanguageSettingsTests
{
    [Theory]
    [InlineData(" EN ", "en")]
    [InlineData("ru", "ru")]
    public async Task SetAsync_ValidLanguage_NormalizesAndWritesOnlyOnce(string argument, string expected)
    {
        var store = new LanguageStore();
        var settings = new UserLanguageSettings(store);
        var owner = new UserId(101);
        using var cancellation = new CancellationTokenSource();

        Assert.Equal(expected, await settings.SetAsync(owner, argument, cancellation.Token));
        Assert.Equal(expected, await settings.SetAsync(owner, expected, cancellation.Token));
        Assert.Equal(expected, await settings.ReadAsync(owner, cancellation.Token));
        Assert.Equal((owner, expected, cancellation.Token), Assert.Single(store.Writes));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en ru")]
    [InlineData("ru-RU")]
    public async Task SetAsync_InvalidLanguage_DoesNotWrite(string argument)
    {
        var store = new LanguageStore { Value = "ru" };
        var settings = new UserLanguageSettings(store);
        var owner = new UserId(101);

        Assert.Null(await settings.SetAsync(owner, argument, default));
        Assert.Equal("ru", await settings.ReadAsync(owner, default));
        Assert.Empty(store.Writes);
    }

    private sealed class LanguageStore : IUserLanguageStore
    {
        public string? Value { get; set; }
        public List<(UserId User, string Language, CancellationToken Token)> Writes { get; } = [];

        public Task<string?> ReadAsync(UserId user, CancellationToken token) => Task.FromResult(Value);

        public Task SetAsync(UserId user, string language, CancellationToken token)
        {
            Writes.Add((user, language, token));
            Value = language;
            return Task.CompletedTask;
        }
    }
}
