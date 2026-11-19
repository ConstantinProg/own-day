namespace OwnDay.Domain.Learning;

public enum LearningPreferenceKind
{
    Format,
    Depth,
    Pace,
    Examples
}

public sealed class LearningPreference
{
    public const int MaxValueLength = 200;
    private LearningPreference() { }
    public UserId UserId { get; private set; }
    public LearningPreferenceKind Kind { get; private set; }
    public string Scope { get; private set; } = "";
    public string Value { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; }
    public string Source { get; private set; } = "Explicit user preference";
    public long? ResetThrough { get; private set; }

    public static LearningPreference Create(UserId user, LearningPreferenceKind kind, string value, DateTimeOffset now, string? discipline = null)
    {
        if (!user.IsValid || !Enum.IsDefined(kind)) throw new ArgumentException("Invalid preference owner or kind.");
        var preference = new LearningPreference { UserId = user, Kind = kind, Scope = ScopeFor(discipline) };
        preference.Change(value, now);
        return preference;
    }

    public void Change(string value, DateTimeOffset now)
    {
        value = value.Trim();
        if (value.Length is < 1 or > MaxValueLength) throw new ArgumentException("Invalid preference value.", nameof(value));
        Value = value;
        UpdatedAt = now.ToUniversalTime();
        Source = "Explicit user preference";
    }

    public bool IsExplicit() => Source == "Explicit user preference";

    public void Reset(long through, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(through);
        Change("default", now);
        Source = "User reset";
        ResetThrough = Math.Max(ResetThrough ?? 0, through);
    }

    public static string ScopeFor(string? discipline)
    {
        var scope = discipline?.Trim().ToUpperInvariant() ?? "";
        if (scope.Length > LearningProgram.MaxDisciplineLength) throw new ArgumentException("Invalid preference scope.", nameof(discipline));
        return scope;
    }
}

public sealed record LearningPreferenceValues(string? Format, string? Depth, string? Pace, string? Examples)
{
    public LearningPreferenceValues WithFallback(LearningPreferenceValues fallback) =>
        new(Format ?? fallback.Format, Depth ?? fallback.Depth, Pace ?? fallback.Pace, Examples ?? fallback.Examples);

    public bool IsEmpty() => Format is null && Depth is null && Pace is null && Examples is null;
}

public enum PedagogicalStrategy
{
    Standard,
    Hint,
    Explain,
    Simplify,
    Practice,
    ReviewPrerequisite,
    Advance,
    Review,
    InterdisciplinaryPractice
}

// A snapshot of the requested teaching approach, not a claim that the provider followed it.
public sealed record LessonTeaching(PedagogicalStrategy Strategy, LearningPreferenceValues Preferences);

public sealed record StrategyObservation(Guid LessonId, PedagogicalStrategy Strategy, bool Correct, bool Conflict);
public sealed record StrategyEffectiveness(PedagogicalStrategy Strategy, int Answers, int Positive, int Negative, int Conflicts)
{
    public bool HasRepeatedEvidence => Answers >= 3;

    public static IReadOnlyList<StrategyEffectiveness> Evaluate(IEnumerable<StrategyObservation> observations) =>
        observations.DistinctBy(o => o.LessonId).GroupBy(o => o.Strategy).OrderBy(g => g.Key)
            .Select(g => new StrategyEffectiveness(g.Key, g.Count(), g.Count(o => o.Correct && !o.Conflict),
                g.Count(o => !o.Correct && !o.Conflict), g.Count(o => o.Conflict))).ToArray();
}
