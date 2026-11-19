using System.Globalization;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

internal sealed record ExplanationPreference(LearningPreferenceKind Kind, string Value, bool IsHabitSignal = true)
{
    public static ExplanationPreference? Parse(string request)
    {
        var text = request.Trim().TrimEnd('.', '!', '?').ToLowerInvariant();
        if (text is "объясни короче" or "покороче" or "explain briefly" or "make it shorter") return new(LearningPreferenceKind.Depth, "brief");
        if (text is "объясни подробнее" or "подробнее" or "explain in more detail" or "more detail") return new(LearningPreferenceKind.Depth, "detailed");
        if (text is "объясни на другом примере" or "другой пример" or "give another example" or "use a different example")
            return new(LearningPreferenceKind.Examples, "a different concrete example");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var minutes = words is ["есть", var count, "минут" or "минуты"] ? count :
            words is ["i", "have", var duration, "minutes" or "minute"] ? duration : null;
        return int.TryParse(minutes, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 60
            ? new(LearningPreferenceKind.Pace, $"fit the explanation in {value} minutes", false) : null;
    }

    public LearningPreferenceValues ForExplanation() => Kind switch
    {
        LearningPreferenceKind.Format => new(Value, null, null, null),
        LearningPreferenceKind.Depth => new(null, Value, null, null),
        LearningPreferenceKind.Pace => new(null, null, Value, null),
        _ => new(null, null, null, Value)
    };
}
