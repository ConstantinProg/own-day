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
    public void EnsureComplete_ChangedBranch_RejectsUnusedSavedResult()
    {
        var session = new LearningProviderSession([new LearningProviderStep()], () => "v1");
        Assert.Throws<StaleLearningPreparationException>(session.EnsureComplete);
    }
}
