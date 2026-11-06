using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public sealed record LearningPersonalization(LearningPreferenceValues ExplicitPreferences,
    IReadOnlyList<StrategyEffectiveness> StrategyEvidence)
{
    public LearningPersonalization ForGeneration() => this with
    {
        StrategyEvidence = StrategyEvidence.Where(s => s.HasRepeatedEvidence && s.Conflicts == 0).ToArray()
    };
}

public sealed partial class KnowledgeService
{
    public async Task<LearningPersonalization> PersonalizationAsync(UserId user, CancellationToken token)
    {
        Validate(user, token);
        var preferences = await store.PreferencesAsync(user, token);
        var observations = await store.StrategyObservationsAsync(user, token);
        token.ThrowIfCancellationRequested();
        string? Value(LearningPreferenceKind kind) => preferences.SingleOrDefault(p => p.Kind == kind)?.Value;
        return new(new(Value(LearningPreferenceKind.Format), Value(LearningPreferenceKind.Depth),
            Value(LearningPreferenceKind.Pace), Value(LearningPreferenceKind.Examples)), StrategyEffectiveness.Evaluate(observations));
    }

    public async Task<bool> SetPreferenceAsync(UserId user, LearningPreferenceKind kind, string value, CancellationToken token)
    {
        Validate(user, token);
        value = value.Trim();
        if (!Enum.IsDefined(kind) || value.Length is < 1 or > LearningPreference.MaxValueLength) return false;
        var existing = (await store.PreferencesAsync(user, token)).SingleOrDefault(p => p.Kind == kind);
        token.ThrowIfCancellationRequested();
        if (existing is null) store.Add(LearningPreference.Create(user, kind, value, clock.GetUtcNow()));
        else existing.Change(value, clock.GetUtcNow());
        return true;
    }

    public async Task<bool> ResetPreferenceAsync(UserId user, LearningPreferenceKind? kind, CancellationToken token)
    {
        Validate(user, token);
        if (kind is not null && !Enum.IsDefined(kind.Value)) return false;
        var preferences = await store.PreferencesAsync(user, token);
        token.ThrowIfCancellationRequested();
        foreach (var preference in preferences.Where(p => kind is null || p.Kind == kind)) store.Remove(preference);
        return true;
    }
}
