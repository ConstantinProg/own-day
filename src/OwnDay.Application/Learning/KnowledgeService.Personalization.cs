using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public sealed record PreferenceSignal(long InteractionId, Guid LessonId, string Request);
public sealed record PreferenceSource(LearningPreferenceKind Kind, string Value, string Scope, string Source);

public sealed record LearningPersonalization(LearningPreferenceValues ExplicitPreferences,
    IReadOnlyList<StrategyEffectiveness> StrategyEvidence)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public LearningPreferenceValues? ObservedPreferences { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<PreferenceSource> Sources { get; init; } = [];

    public LearningPreferenceValues EffectivePreferences() => ObservedPreferences is { } observed
        ? ExplicitPreferences.WithFallback(observed) : ExplicitPreferences;

    public LearningPersonalization ForGeneration() => this with
    {
        StrategyEvidence = StrategyEvidence.Where(s => s.HasRepeatedEvidence && s.Conflicts == 0).ToArray()
    };
}

public sealed partial class KnowledgeService
{
    public async Task<LearningPersonalization> PersonalizationAsync(UserId user, CancellationToken token, string? discipline = null)
    {
        Validate(user, token);
        var preferences = await store.PreferencesAsync(user, token);
        var observations = await store.StrategyObservationsAsync(user, token, discipline);
        var signals = await store.PreferenceSignalsAsync(user, discipline, token);
        token.ThrowIfCancellationRequested();
        var scope = LearningPreference.ScopeFor(discipline);
        var applicable = preferences.Where(item => item.Scope == "" || item.Scope == scope).ToArray();
        var observed = Observe(signals, applicable, scope);
        string? Value(LearningPreferenceKind kind) => applicable.Where(item => item.Kind == kind && item.IsExplicit())
            .OrderByDescending(item => item.Scope == scope).FirstOrDefault()?.Value;
        string? Observed(LearningPreferenceKind kind) => observed.SingleOrDefault(item => item.Kind == kind)?.Value;
        var inferred = new LearningPreferenceValues(Observed(LearningPreferenceKind.Format), Observed(LearningPreferenceKind.Depth),
            Observed(LearningPreferenceKind.Pace), Observed(LearningPreferenceKind.Examples));
        return new(new(Value(LearningPreferenceKind.Format), Value(LearningPreferenceKind.Depth),
            Value(LearningPreferenceKind.Pace), Value(LearningPreferenceKind.Examples)), StrategyEffectiveness.Evaluate(observations))
        {
            ObservedPreferences = inferred.IsEmpty() ? null : inferred,
            Sources = [.. applicable.Where(item => item.IsExplicit()).Select(item => new PreferenceSource(item.Kind, item.Value, item.Scope, item.Source)), .. observed]
        };
    }

    public async Task<bool> SetPreferenceAsync(UserId user, LearningPreferenceKind kind, string value, CancellationToken token, string? discipline = null)
    {
        Validate(user, token);
        value = value.Trim();
        if (!Enum.IsDefined(kind) || value.Length is < 1 or > LearningPreference.MaxValueLength) return false;
        var scope = LearningPreference.ScopeFor(discipline);
        var existing = (await store.PreferencesAsync(user, token)).SingleOrDefault(p => p.Kind == kind && p.Scope == scope);
        token.ThrowIfCancellationRequested();
        if (existing is null) store.Add(LearningPreference.Create(user, kind, value, clock.GetUtcNow(), discipline));
        else existing.Change(value, clock.GetUtcNow());
        return true;
    }

    public async Task<bool> ResetPreferenceAsync(UserId user, LearningPreferenceKind? kind, CancellationToken token, string? discipline = null)
    {
        Validate(user, token);
        if (kind is not null && !Enum.IsDefined(kind.Value)) return false;
        var preferences = await store.PreferencesAsync(user, token);
        var signals = await store.PreferenceSignalsAsync(user, discipline, token);
        token.ThrowIfCancellationRequested();
        var scope = LearningPreference.ScopeFor(discipline);
        var observed = signals.Select(signal => (signal.InteractionId, Preference: ExplanationPreference.Parse(signal.Request)))
            .Where(item => item.Preference is { IsHabitSignal: true }).ToArray();
        var now = clock.GetUtcNow();
        foreach (var target in kind is { } one ? [one] : Enum.GetValues<LearningPreferenceKind>())
        {
            var existing = preferences.SingleOrDefault(item => item.Kind == target && item.Scope == scope);
            var signalEnd = observed.Where(item => item.Preference!.Kind == target).Select(item => item.InteractionId).DefaultIfEmpty().Max();
            var through = Math.Max(signalEnd, existing?.ResetThrough ?? 0);
            if (through == 0)
            {
                if (existing is not null) store.Remove(existing);
                continue;
            }
            var reset = existing ?? LearningPreference.Create(user, target, "default", now, discipline);
            reset.Reset(through, now);
            if (existing is null) store.Add(reset);
        }
        return true;
    }

    private static PreferenceSource[] Observe(IReadOnlyList<PreferenceSignal> signals, LearningPreference[] preferences, string scope) =>
        signals.Select(signal => (Signal: signal, Preference: ExplanationPreference.Parse(signal.Request)))
            .Where(item => item.Preference is { IsHabitSignal: true } && item.Signal.InteractionId >
                (preferences.SingleOrDefault(preference => preference.Scope == scope && preference.Kind == item.Preference.Kind)?.ResetThrough ?? 0))
            .GroupBy(item => item.Preference!.Kind)
            .Select(group => new
            {
                Kind = group.Key,
                Replies = group.ToArray()
            })
            .Where(group => group.Replies.Select(item => item.Signal.LessonId).Distinct().Count() >= 3 &&
                group.Replies.Select(item => item.Preference!.Value).Distinct(StringComparer.Ordinal).Count() == 1)
            .Select(group => new PreferenceSource(group.Kind, group.Replies[0].Preference!.Value, scope, "Repeated explanation requests"))
            .ToArray();
}
