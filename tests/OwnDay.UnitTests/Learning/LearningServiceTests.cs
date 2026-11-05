using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LearningServiceTests
{
    [Fact]
    public async Task HandleAsync_Canceled_DoesNotAccessStore()
    {
        var store = new Store();
        var service = new LearningService(store, new Teacher());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.HandleAsync(new UserId(1), true, "Topic", cancellation.Token));
        Assert.False(store.Accessed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_InvalidOrFailedAssessment_PreservesActiveLesson(bool failure)
    {
        var store = new Store { Active = Lesson.Start(new UserId(1), "Topic", "Theory", "Question") };
        var service = new LearningService(store, new Teacher { Failure = failure });
        var replies = await service.HandleAsync(new UserId(1), false, "Answer", CancellationToken.None);
        Assert.Null(store.Active.Answer);
        Assert.Contains("Retry", Assert.Single(replies));
    }

    private sealed class Store : ILessonStore
    {
        public Lesson? Active { get; init; }
        public bool Accessed { get; private set; }
        public Task<Lesson?> FindActiveAsync(UserId user, CancellationToken token)
        {
            Accessed = true;
            return Task.FromResult(Active);
        }
        public void Add(Lesson lesson) => throw new InvalidOperationException();
    }

    private sealed class Teacher : ILessonTeacher
    {
        public bool Failure { get; init; }
        public Task<LessonMaterial> GenerateAsync(string topic, CancellationToken token) => throw new InvalidOperationException();
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token) =>
            Failure ? throw new LessonProviderException() : Task.FromResult(new LessonAssessment("", false));
    }
}
