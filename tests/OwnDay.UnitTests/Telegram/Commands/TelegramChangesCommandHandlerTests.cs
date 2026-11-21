using OwnDay.Infrastructure.Changelog;
using OwnDay.Infrastructure.Telegram.Commands;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Commands;

public sealed class TelegramChangesCommandHandlerTests
{
    [Theory]
    [InlineData("1.2.0")]
    [InlineData("1.2.0-rc.1+build.42")]
    public void FindNotes_WithMultipleEntries_ReturnsOnlyExactVersion(string version)
    {
        var markdown = $"""
            # История изменений

            ## [Unreleased]
            ### Добавлено
            - Будущая функция

            ## [1.3.0] - 2026-11-21
            ### Добавлено
            - Новая функция

            ## [{version}] - 2026-11-20
            ### Добавлено
            - Новая команда `/test`
            ### Изменено
            - Уточнена справка
            ### Added
            - New `/test` command

            ## [1.1.0] - 2026-11-19
            ### Удалено
            - Старая команда
            """;

        Assert.Equal("Добавлено\n• Новая команда /test\nИзменено\n• Уточнена справка",
            ReleaseChangelog.FindNotes(markdown, version, "ru"));
        Assert.Equal("Added\n• New /test command", ReleaseChangelog.FindNotes(markdown, version, "en"));
        Assert.Null(ReleaseChangelog.FindNotes(markdown, "Unreleased", "ru"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("## [2.0.0] - yesterday\n### Добавлено\n- Новая команда")]
    [InlineData("## [2.0.0] - 2026-11-21\n### Добавлено")]
    [InlineData("## [1.9.0] - 2026-11-21\n### Исправлено\n- Старая ошибка")]
    [InlineData("## [Unreleased]\n### Добавлено\n- Будущая функция")]
    public void Render_NoUsableEntry_PreservesVersionAndHistoryLink(string markdown)
    {
        var notes = ReleaseChangelog.FindNotes(markdown, "2.0.0", "ru");
        Assert.Null(notes);

        var message = TelegramChangesCommandHandler.Render("2.0.0", notes, "ru");

        Assert.StartsWith("OwnDay 2.0.0\n", message);
        Assert.Contains("Для этой версии пока нет записи", message);
        Assert.EndsWith("https://github.com/ConstantinProg/own-day/blob/main/CHANGELOG.md", message);
    }

    [Theory]
    [InlineData("ru", "Остальные изменения доступны по ссылке")]
    [InlineData("en", "See the full changelog for the remaining changes")]
    public void Render_NotesExceedTelegramLimit_RetainsVersionAndLinkWithoutBreakingUnicode(string locale, string notice)
    {
        var notes = "x" + string.Concat(Enumerable.Repeat("😀", 4096));

        var message = TelegramChangesCommandHandler.Render("2.0.0", notes, locale);

        Assert.True(message.Length <= 4096);
        Assert.StartsWith("OwnDay 2.0.0\n", message);
        Assert.Contains(notice, message);
        Assert.EndsWith("https://github.com/ConstantinProg/own-day/blob/main/CHANGELOG.md", message);
        Assert.Equal(message, new System.Text.UTF8Encoding(false, true).GetString(
            new System.Text.UTF8Encoding(false, true).GetBytes(message)));
    }
}
