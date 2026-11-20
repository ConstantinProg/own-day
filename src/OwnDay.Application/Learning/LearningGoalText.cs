using System.Text.RegularExpressions;

namespace OwnDay.Application.Learning;

public static partial class LearningGoalText
{
    // A reviewed correction is restricted to standalone prose, preserving identifiers and code.
    [GeneratedRegex(@"(?<![\p{L}\p{N}_./@#`-])[Ии]нвистиции(?![\p{L}\p{N}_/@#`-]|\.[\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex InvestmentTypo();

    [GeneratedRegex(@"[\p{L}\p{N}_]+|[^\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    public static string Display(string text)
    {
        if (text.Contains('`') || text.Contains("://", StringComparison.Ordinal)) return text;
        return InvestmentTypo().Replace(text, match => char.IsUpper(match.Value[0]) ? "Инвестиции" : "инвестиции");
    }

    internal static string? AcceptCorrection(string original, string? proposal, int maximumLength)
    {
        var reviewed = Display(original);
        if (reviewed != original) return reviewed;
        if (proposal is null || proposal == original || proposal.Length > maximumLength || Math.Abs(proposal.Length - original.Length) > 3 ||
            original.IndexOfAny(['`', '/', '\\', '_', '@', '#', '{', '}', '<', '>', '\'', '"', '«', '»']) >= 0)
            return null;
        var before = Tokens().Matches(original);
        var after = Tokens().Matches(proposal);
        if (before.Count != after.Count) return null;
        var changes = 0;
        for (var i = 0; i < before.Count; i++)
        {
            var word = before[i].ValueSpan;
            var corrected = after[i].ValueSpan;
            if (word.SequenceEqual(corrected)) continue;
            if (++changes > 3 || !IsSmallCorrection(word, corrected)) return null;
        }
        return proposal;
    }

    private static bool IsSmallCorrection(ReadOnlySpan<char> original, ReadOnlySpan<char> corrected)
    {
        if (!IsLowercaseRussianWord(original) || !IsLowercaseRussianWord(corrected) ||
            !original[..2].SequenceEqual(corrected[..2]) || Math.Abs(original.Length - corrected.Length) > 1)
            return false;
        var first = 2;
        while (first < Math.Min(original.Length, corrected.Length) && original[first] == corrected[first]) first++;
        if (original.Length > corrected.Length) return original[(first + 1)..].SequenceEqual(corrected[first..]);
        if (corrected.Length > original.Length) return original[first..].SequenceEqual(corrected[(first + 1)..]);
        return original[(first + 1)..].SequenceEqual(corrected[(first + 1)..]) ||
            first + 1 < original.Length && original[first] == corrected[first + 1] && original[first + 1] == corrected[first] &&
            original[(first + 2)..].SequenceEqual(corrected[(first + 2)..]);
    }

    private static bool IsLowercaseRussianWord(ReadOnlySpan<char> word)
    {
        if (word.Length < 5) return false;
        foreach (var letter in word)
            if (letter is not (>= 'а' and <= 'я' or 'ё')) return false;
        return true;
    }
}
