namespace OwnDay.Application.Learning;

internal static class LearnerExperience
{
    private static readonly string[] _experienceLabels = ["Опыт: ", "Experience: "];
    private static readonly string[] _practicalUseLabels = ["Применение: ", "Application: "];
    public static bool IsBeginner(string value) => value.Trim().TrimEnd('.', '!').ToLowerInvariant() is
        "с нуля" or "начинаю с нуля" or "начинающий" or "новичок" or "нет опыта" or "beginner" or "no experience" or
        "starting from scratch" or "i'm starting from scratch";

    public static string ReadSelfReport(string experience)
    {
        // Existing programs store either a raw self-report or the last line of a consultation summary.
        var lastLine = experience[(experience.LastIndexOf('\n') + 1)..].Trim();
        foreach (var prefix in _experienceLabels)
        {
            if (lastLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return lastLine[prefix.Length..];
        }
        return lastLine;
    }

    public static string? ReadPracticalUse(string experience)
    {
        var newline = experience.LastIndexOf('\n');
        if (newline < 0 || !_experienceLabels.Any(label =>
                experience[(newline + 1)..].Trim().StartsWith(label, StringComparison.OrdinalIgnoreCase))) return null;
        var first = experience[..newline].Trim();
        foreach (var prefix in _practicalUseLabels)
        {
            if (first.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && first.Length > prefix.Length)
                return first[prefix.Length..];
        }
        return null;
    }
}
