using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LearningPersonalizationTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("too long")]
    public void Change_InvalidPreference_RejectsWithoutMutation(string value)
    {
        var preference = LearningPreference.Create(new(1), LearningPreferenceKind.Depth, "short", DateTimeOffset.UtcNow);
        if (value == "too long") value = new('x', 201);
        Assert.Throws<ArgumentException>(() => preference.Change(value, DateTimeOffset.UtcNow));
        Assert.Equal("short", preference.Value);
    }

    [Fact]
    public async Task SetAndResetPreferenceAsync_ExplicitChoice_UpdatesOneKindAndIsolatesOwners()
    {
        var store = new KnowledgeStore();
        var service = new KnowledgeService(store, TimeProvider.System);
        Assert.True(await service.SetPreferenceAsync(new(1), LearningPreferenceKind.Format, "examples", default));
        Assert.True(await service.SetPreferenceAsync(new(1), LearningPreferenceKind.Depth, "brief", default));
        Assert.True(await service.SetPreferenceAsync(new(1), LearningPreferenceKind.Format, "dialogue", default));
        Assert.True(await service.SetPreferenceAsync(new(2), LearningPreferenceKind.Format, "private", default));
        Assert.Equal("dialogue", (await service.PersonalizationAsync(new(1), default)).ExplicitPreferences.Format);
        Assert.Equal("Explicit user preference", store.Preferences[0].Source);
        await service.ResetPreferenceAsync(new(1), LearningPreferenceKind.Format, default);
        var profile = await service.PersonalizationAsync(new(1), default);
        Assert.Null(profile.ExplicitPreferences.Format);
        Assert.Equal("brief", profile.ExplicitPreferences.Depth);
        await service.ResetPreferenceAsync(new(1), null, default);
        Assert.Equal(new UserId(2), Assert.Single(store.Preferences).UserId);
    }

    [Fact]
    public void Evaluate_DuplicateSingleOrConflictingObservation_DoesNotSupportStrategySuggestion()
    {
        var observation = new StrategyObservation(Guid.NewGuid(), PedagogicalStrategy.Simplify, true, false);
        var one = Assert.Single(StrategyEffectiveness.Evaluate([observation, observation]));
        Assert.Equal(1, one.Answers);
        Assert.False(one.HasRepeatedEvidence);
        var profile = new LearningPersonalization(new("dialogue", null, null, null), [one]);
        Assert.Empty(profile.ForGeneration().StrategyEvidence);
        var repeated = StrategyEffectiveness.Evaluate([observation, observation with { LessonId = Guid.NewGuid() },
            observation with { LessonId = Guid.NewGuid(), Correct = false, Conflict = true }]);
        var summary = Assert.Single(repeated);
        Assert.True(summary.HasRepeatedEvidence);
        Assert.Equal(2, summary.Positive);
        Assert.Equal(0, summary.Negative);
        Assert.Equal(1, summary.Conflicts);
        Assert.Empty((profile with { StrategyEvidence = repeated }).ForGeneration().StrategyEvidence);
    }

    [Fact]
    public async Task PersonalizationAsync_RepeatedEvidence_PreservesExplicitPreferencesWithoutInferringTraits()
    {
        var store = new KnowledgeStore();
        store.StrategyObservations.AddRange(Enumerable.Range(0, 3).Select(_ =>
            new StrategyObservation(Guid.NewGuid(), PedagogicalStrategy.Simplify, true, false)));
        var service = new KnowledgeService(store, TimeProvider.System);
        await service.SetPreferenceAsync(new(1), LearningPreferenceKind.Depth, "in depth", default);
        var profile = (await service.PersonalizationAsync(new(1), default)).ForGeneration();
        Assert.Equal("in depth", profile.ExplicitPreferences.Depth);
        Assert.Null(profile.ExplicitPreferences.Format);
        Assert.Equal(3, Assert.Single(profile.StrategyEvidence).Positive);
        Assert.Single(store.Preferences);
    }

    [Fact]
    public async Task PreferenceOperations_CanceledOrInvalidUser_RejectBeforeMutation()
    {
        var store = new KnowledgeStore();
        var service = new KnowledgeService(store, TimeProvider.System);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PersonalizationAsync(new(1), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetPreferenceAsync(new(1), LearningPreferenceKind.Pace, "slow", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ResetPreferenceAsync(new(1), null, canceled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PersonalizationAsync(default, default));
        Assert.False(await service.SetPreferenceAsync(new(1), (LearningPreferenceKind)99, "slow", default));
        Assert.False(await service.ResetPreferenceAsync(new(1), (LearningPreferenceKind)99, default));
        Assert.Empty(store.Preferences);
    }
}
