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
        using var reader = new StringReader(markdown);
        if (string.Equals(version, "Unreleased", StringComparison.OrdinalIgnoreCase) || !MoveToRelease(reader, version))
        {
            return null;
        }

        List<string> notes = [];
        string? category = null;
        var hasBullets = false;
        while (reader.ReadLine()?.Trim() is { } line && !line.StartsWith("## ", StringComparison.Ordinal))
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                var heading = line[4..];
                category = IsLocalizedCategory(heading, locale) ? heading : null;
                hasBullets = false;
            }
            else if (category is not null && line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (!hasBullets)
                {
                    notes.Add(category);
                    hasBullets = true;
                }

                notes.Add("• " + line[2..].Replace("`", string.Empty, StringComparison.Ordinal));
            }
        }

        return notes.Count == 0 ? null : string.Join('\n', notes);
    }

    private static bool MoveToRelease(StringReader reader, string version)
    {
        var prefix = $"## [{version}] - ";
        while (reader.ReadLine()?.Trim() is { } line)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal) &&
                DateOnly.TryParseExact(line.AsSpan(prefix.Length), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLocalizedCategory(string heading, string locale) => (locale, heading) is
        ("ru", "Добавлено" or "Изменено" or "Исправлено" or "Удалено") or
        ("en", "Added" or "Changed" or "Fixed" or "Removed");
}
