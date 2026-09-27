using System.Collections;
using System.Globalization;
using System.Resources;
using OwnDay.Infrastructure.Telegram.Localization;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Localization;

public sealed class TelegramTextsTests
{
    [Fact]
    public void Resources_EnglishAndRussian_HaveMatchingKeysAndValidFormats()
    {
        var manager = new ResourceManager(
            "OwnDay.Infrastructure.Telegram.Localization.TelegramMessages",
            typeof(TelegramTexts).Assembly);
        var english = ReadValues(manager, CultureInfo.InvariantCulture);
        var russian = ReadValues(manager, CultureInfo.GetCultureInfo("ru"));

        Assert.NotEmpty(english);
        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), russian.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, value) in english)
        {
            _ = string.Format(CultureInfo.InvariantCulture, value, 1, 2, 3, 4, 5);
            _ = string.Format(CultureInfo.GetCultureInfo("ru"), russian[key], 1, 2, 3, 4, 5);
        }
    }

    [Theory]
    [InlineData("ru", "ru")]
    [InlineData("ru-RU", "ru")]
    [InlineData("en", "en")]
    [InlineData("de", "en")]
    [InlineData(null, "en")]
    public void ResolveLocale_TelegramLanguage_UsesSupportedLanguageOrEnglish(string? languageCode, string expected)
    {
        Assert.Equal(expected, TelegramTexts.ResolveLocale(languageCode));
    }

    private static Dictionary<string, string> ReadValues(ResourceManager manager, CultureInfo culture)
    {
        var resources = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(resources);
        return resources.Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);
    }
}
