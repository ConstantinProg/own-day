using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LearningServiceTests
{
    [Theory]
    [InlineData("ask")]
    [InlineData("explain")]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("finish")]
    public async Task HandleAsync_CanceledDialogueCommand_DoesNotAccessStore(string action)
    {
        var store = new Store();
        var service = new LearningService(store, new Teacher());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action switch
        {
            "ask" => service.ClarifyAsync(new UserId(1), "Question", cancellation.Token),
            "explain" => service.ExplainAsync(new UserId(1), cancellation.Token),
            "pause" => service.PauseAsync(new UserId(1), cancellation.Token),
            "resume" => service.ResumeAsync(new UserId(1), cancellation.Token),
            _ => service.FinishAsync(new UserId(1), cancellation.Token)
        });
        Assert.False(store.Accessed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplainAsync_InvalidOrFailedOutput_DoesNotChangeLesson(bool failure)
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        var service = new LearningService(new Store { Active = lesson }, new Teacher { Failure = failure });
        Assert.IsType<LearningResult.ProviderUnavailable>(await service.ExplainAsync(new UserId(1), CancellationToken.None));
        Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
        Assert.Empty(lesson.Interactions);
    }

    [Fact]
    public async Task ClarifyAsync_ProviderCancelsCaller_DoesNotRecordExplanation()
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        using var cancellation = new CancellationTokenSource();
        var teacher = new Teacher { Cancel = cancellation };
        var service = new LearningService(new Store { Active = lesson }, teacher);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ClarifyAsync(new UserId(1), "Question", cancellation.Token));
        Assert.Equal(cancellation.Token, teacher.ReceivedToken);
        Assert.Empty(lesson.Interactions);
    }

    [Fact]
    public async Task HandleAsync_PausedLesson_RejectsAnswerAndExplanationUntilResume()
    {
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        lesson.Pause();
        var service = new LearningService(new Store { Active = lesson }, new Teacher());
        Assert.IsType<LearningResult.InvalidTransition>(await service.AnswerAsync(new UserId(1), "Answer", CancellationToken.None));
        Assert.IsType<LearningResult.InvalidTransition>(await service.ExplainAsync(new UserId(1), CancellationToken.None));
        Assert.IsType<LearningResult.InvalidTransition>(await service.PauseAsync(new UserId(1), CancellationToken.None));
        Assert.Equal(LessonStatus.Paused, Assert.IsType<LearningResult.Session>(await service.StartAsync(new UserId(1), "Other", CancellationToken.None)).Status);
        Assert.Equal(LessonStatus.AwaitingAnswer, Assert.IsType<LearningResult.Session>(await service.ResumeAsync(new UserId(1), CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("oversized")]
    public async Task ClarifyAsync_InvalidRequest_DoesNotAccessStore(string request)
    {
        var store = new Store();
        var service = new LearningService(store, new Teacher());
        if (request == "oversized") request = new string('q', 1001);
        Assert.IsType<LearningResult.InvalidClarification>(await service.ClarifyAsync(new UserId(1), request, CancellationToken.None));
        Assert.False(store.Accessed);
    }

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
        Assert.Equal(new LearningResult.Session(lesson.ContextSummary, LessonStatus.AwaitingAnswer), result);
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
        public CancellationTokenSource? Cancel { get; init; }
        public CancellationToken ReceivedToken { get; private set; }
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) =>
            Explain(token);

        private Task<LessonExplanation> Explain(CancellationToken token)
        {
            ReceivedToken = token;
            Cancel?.Cancel();
            if (Failure) throw new LessonProviderException();
            return Task.FromResult(new LessonExplanation(Cancel is null ? "" : "Explanation"));
        }
        public bool Failure { get; init; }
        public Task<LessonMaterial> GenerateAsync(string topic, CancellationToken token) => throw new InvalidOperationException();
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token) =>
            Failure ? throw new LessonProviderException() : Task.FromResult(new LessonAssessment("", false));
    }
}
