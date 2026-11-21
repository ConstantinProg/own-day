using System.Globalization;

namespace OwnDay.Infrastructure.Changelog;

public static class ReleaseChangelog
{
    public static async Task<string?> ReadNotesAsync(string version, string locale, CancellationToken cancellationToken)
    {
        using var stream = typeof(ReleaseChangelog).Assembly.GetManifestResourceStream("OwnDay.CHANGELOG.md");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return FindNotes(await reader.ReadToEndAsync(cancellationToken), version, locale);
    }

    public static string? FindNotes(string markdown, string version, string locale)
    {
        if (string.Equals(version, "Unreleased", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var reader = new StringReader(markdown);
        var releasePrefix = $"## [{version}] - ";
        var inRelease = false;
        string? category = null;
        string? shownCategory = null;
        List<string> notes = [];

        while (reader.ReadLine()?.Trim() is { } line)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inRelease)
                {
                    break;
                }

                inRelease = line.StartsWith(releasePrefix, StringComparison.Ordinal) &&
                    DateOnly.TryParseExact(line.AsSpan(releasePrefix.Length), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            }
            else if (inRelease && line.StartsWith("### ", StringComparison.Ordinal))
            {
                category = (locale, line[4..]) switch
                {
                    ("ru", "Добавлено" or "Изменено" or "Исправлено" or "Удалено") => line[4..],
                    ("en", "Added" or "Changed" or "Fixed" or "Removed") => line[4..],
                    _ => null
                };
            }
            else if (inRelease && category is not null && line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (shownCategory != category)
                {
                    notes.Add(category);
                    shownCategory = category;
                }
                notes.Add("• " + line[2..].Replace("`", string.Empty, StringComparison.Ordinal));
            }
        }

        return notes.Count == 0 ? null : string.Join('\n', notes);
    }
}
