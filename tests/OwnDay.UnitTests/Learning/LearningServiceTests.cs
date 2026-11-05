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
            service.StartAsync(new UserId(1), "Topic", cancellation.Token));
        Assert.False(store.Accessed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_InvalidOrFailedAssessment_PreservesActiveLesson(bool failure)
    {
        var store = new Store { Active = Lesson.Start(new UserId(1), "Topic", "Theory", "Question") };
        var service = new LearningService(store, new Teacher { Failure = failure });
        var result = await service.AnswerAsync(new UserId(1), "Answer", CancellationToken.None);
        Assert.Null(store.Active.Answer);
        Assert.IsType<LearningResult.ProviderUnavailable>(result);
    }

    [Fact]
    public async Task StartAsync_ActiveLesson_ReturnsExistingMaterialWithoutGeneration()
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        var service = new LearningService(new Store { Active = lesson }, new Teacher());
        var result = await service.StartAsync(new UserId(1), "Different topic", CancellationToken.None);
        Assert.Equal(new LearningResult.Material("Theory", "Question"), result);
    }

    [Fact]
    public async Task AnswerAsync_NoActiveLesson_ReturnsMissingLesson()
    {
        var service = new LearningService(new Store(), new Teacher());
        var result = await service.AnswerAsync(new UserId(1), "Answer", CancellationToken.None);
        Assert.IsType<LearningResult.NoActiveLesson>(result);
    }

    [Fact]
    public async Task StartAsync_InvalidTopic_DoesNotAccessStore()
    {
        var store = new Store();
        var service = new LearningService(store, new Teacher());
        Assert.IsType<LearningResult.InvalidTopic>(await service.StartAsync(new UserId(1), " ", CancellationToken.None));
        Assert.False(store.Accessed);
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
