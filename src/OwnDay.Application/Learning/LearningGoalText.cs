using System.Text.RegularExpressions;

namespace OwnDay.Application.Learning;

public static partial class LearningGoalText
{
    // A reviewed correction is restricted to standalone prose, preserving identifiers and code.
    [GeneratedRegex(@"(?<![\p{L}\p{N}_./@#`-])(?:[Ии]нвистиции|[Пп]ортфлеь)(?![\p{L}\p{N}_/@#`-]|\.[\p{L}\p{N}])",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReviewedTypos();

    public static string Display(string text)
    {
        if (text.Contains('`') || text.Contains("://", StringComparison.Ordinal)) return text;
        return ReviewedTypos().Replace(text, match => match.Value switch
        {
            "инвистиции" => "инвестиции",
            "Инвистиции" => "Инвестиции",
            "портфлеь" => "портфель",
            "Портфлеь" => "Портфель",
            _ => match.Value
        });
    }
}
