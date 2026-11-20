using OwnDay.Application.Dialogue;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LearningProviderSessionTests
{
    [Fact]
    public async Task Assess_Prepare_CapturesImmutableOwnedLessonWithoutCallingProvider()
    {
        var lesson = Lesson.Start(new UserId(101), "English", "Read this", "What is requested?");
        var session = new LearningProviderSession([], () => "targets-v1");
        var pending = await Assert.ThrowsAsync<LearningProviderRequiredException>(() => session.AssessAsync(lesson, "A reply", default));
        lesson.Pause();
        Assert.Equal("assess", pending.Step.Kind);
        var input = pending.Step.ReadInput<LessonAssessmentInput>();
        Assert.Equal(lesson.Id, input.Lesson.Id);
        Assert.Equal(LessonStatus.AwaitingAnswer, input.Lesson.Status);
        Assert.Equal("A reply", input.Answer);
        Assert.Equal("targets-v1", pending.Step.TargetStamp);
    }

    [Fact]
    public async Task Generate_SavedResult_ReusesOnlyExactInputAndTargets()
    {
        var session = new LearningProviderSession([], () => "v1");
        var request = new LessonRequest("English");
        var pending = await Assert.ThrowsAsync<LearningProviderRequiredException>(() => session.GenerateAsync(request, default));
        var step = pending.Step.WithResult(new LessonMaterial("Theory", "Question"));
        var replay = new LearningProviderSession([step], () => "v1");
        Assert.Equal(new LessonMaterial("Theory", "Question"), await replay.GenerateAsync(request, default));
        replay.EnsureComplete();
        var stale = new LearningProviderSession([step], () => "v2");
        await Assert.ThrowsAsync<StaleLearningPreparationException>(() => stale.GenerateAsync(request, default));
        var changed = new LearningProviderSession([step], () => "v1");
        await Assert.ThrowsAsync<StaleLearningPreparationException>(() => changed.GenerateAsync(new LessonRequest("Other"), default));
    }

    [Fact]
    public async Task Generate_SavedOpenResultBeforeChoiceSupport_ReplaysWithoutChangingExercise()
    {
        var original = new LearningProviderSession([], () => "target");
        var request = new LessonRequest("Algebra");
        var pending = await Assert.ThrowsAsync<LearningProviderRequiredException>(() => original.GenerateAsync(request, default));
        var openQuestion = new LessonMaterial("Theory", "Explain linear equations.");
        var saved = pending.Step.WithResult(openQuestion);
        var replay = new LearningProviderSession([saved], () => "target");

        var material = await replay.GenerateAsync(request with { PreferredChoice = ChoiceFormat.SingleChoice }, default);

        Assert.Equal(openQuestion, material);
        replay.EnsureComplete();
    }

    [Fact]
    public async Task Resolve_PreparedIntentAcrossRestart_PreservesBudgetAndRejectsChangedPolicy()
    {
        var input = new LearningIntentInput("Can we keep going?", "", "Learn algebra", "Math", [],
            [LearningIntent.Ambiguous, LearningIntent.Next]);
        var initial = new LearningProviderSession([], () => "targets-v1");
        var pending = await Assert.ThrowsAsync<LearningProviderRequiredException>(() => initial.ResolveAsync(input, default));
        var savedIntent = pending.Step.WithResult(new LearningIntentProposal(LearningIntent.Next));
        var restarted = new LearningProviderSession([savedIntent], () => "targets-v1");
        Assert.Equal(LearningIntent.Next, (await restarted.ResolveAsync(input, default)).Intent);
        Assert.Equal(1, restarted.RemainingSteps);
        var generation = await Assert.ThrowsAsync<LearningProviderRequiredException>(() => restarted.GenerateAsync(new("Algebra"), default));
        Assert.Equal(1, generation.Step.Ordinal);

        var upgraded = new LearningProviderSession([savedIntent], () => "targets-v1");
        await Assert.ThrowsAsync<StaleLearningPreparationException>(() =>
            upgraded.ResolveAsync(input with { Allowed = [.. input.Allowed, LearningIntent.PreviousTopic] }, default));
    }

    [Fact]
    public void EnsureComplete_ChangedBranch_RejectsUnusedSavedResult()
    {
        var session = new LearningProviderSession([new LearningProviderStep()], () => "v1");
        Assert.Throws<StaleLearningPreparationException>(session.EnsureComplete);
    }
}
