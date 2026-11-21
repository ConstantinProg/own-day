namespace OwnDay.Application.Learning;

internal static class LearnerExperience
{
    public static bool IsBeginner(string value) => value.Trim().TrimEnd('.', '!').ToLowerInvariant() is
        "с нуля" or "начинаю с нуля" or "начинающий" or "новичок" or "нет опыта" or "beginner" or "no experience" or
        "starting from scratch" or "i'm starting from scratch";

    public static string ReadSelfReport(string experience)
    {
        // Existing programs store either a raw self-report or the last line of a consultation summary.
        var lastLine = experience[(experience.LastIndexOf('\n') + 1)..].Trim();
        foreach (var prefix in new[] { "Опыт: ", "Experience: " })
        {
            if (lastLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return lastLine[prefix.Length..];
        }
        return lastLine;
    }
}
