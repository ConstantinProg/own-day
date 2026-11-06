using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class KnowledgeServiceTests
{
    [Fact]
    public async Task RecordAnswerAsync_ExistingSchedule_UpdatesWithoutCreatingDuplicate()
    {
        var store = new KnowledgeStore();
        var skill = Skill.Create(new UserId(1), "Topic");
        store.Add(skill);
        var initial = ReviewScheduleTests.Answer(skill, DateTimeOffset.UnixEpoch.AddDays(-1), true);
        var schedule = ReviewSchedule.FromAnswer(initial);
        store.Add(schedule);
        var lesson = Lesson.Start(skill.UserId, skill.Title, "Context", "Question");
        lesson.LinkToSkill(skill);
        lesson.MarkAsReview();
        lesson.Complete("Answer", "Feedback", true);

        await new KnowledgeService(store, new Clock()).RecordAnswerAsync(lesson, "test-model", CancellationToken.None);

        Assert.Same(schedule, Assert.Single(store.Reviews));
        Assert.Equal(lesson.Id, schedule.LastLessonId);
        Assert.Equal(1, schedule.SuccessfulReviews);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(3), schedule.DueAt);
        Assert.Equal(lesson.Id, Assert.Single(store.Evidence).LessonId);
    }

    [Fact]
    public async Task RecordAnswerAsync_CanceledRequest_DoesNotCreateScheduleOrEvidence()
    {
        var store = new KnowledgeStore();
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        lesson.LinkToSkill(Skill.Create(lesson.UserId, "Topic"));
        lesson.Complete("Answer", "Feedback", true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new KnowledgeService(store, new Clock()).RecordAnswerAsync(lesson, "test-model", cancellation.Token));

        Assert.Empty(store.Reviews);
        Assert.Empty(store.Evidence);
    }

    [Fact]
    public async Task AnswerAsync_AssessedAnswer_RecordsObservationAndProvenanceAtControlledTime()
    {
        var store = new KnowledgeStore();
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        var service = new LearningService(new Lessons(lesson), new Teacher(), new KnowledgeService(store, new Clock()));
        Assert.IsType<LearningResult.Assessment>(await service.AnswerAsync(new UserId(1), " Answer ", CancellationToken.None));
        var evidence = Assert.Single(store.Evidence);
        Assert.Equal(lesson.Id, evidence.LessonId);
        Assert.Equal(Assert.Single(store.Skills).Id, evidence.SkillId);
        Assert.Equal("Answer", evidence.Answer);
        Assert.Equal("Feedback", evidence.AssessmentFeedback);
        Assert.Equal("test-model", evidence.AssessmentSource);
        Assert.Equal(DateTimeOffset.UnixEpoch, evidence.ObservedAt);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("invalid feedback")]
    [InlineData("invalid source")]
    [InlineData("provider failure")]
    public async Task AnswerAsync_RejectedAssessment_DoesNotCreateSkillOrEvidence(string failure)
    {
        var store = new KnowledgeStore();
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        using var cancellation = new CancellationTokenSource();
        var teacher = new Teacher { Failure = failure, Cancellation = cancellation };
        var service = new LearningService(new Lessons(lesson), teacher, new KnowledgeService(store, new Clock()));
        if (failure == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AnswerAsync(new UserId(1), "Answer", cancellation.Token));
        else Assert.IsType<LearningResult.ProviderUnavailable>(await service.AnswerAsync(new UserId(1), "Answer", cancellation.Token));
        Assert.Equal(cancellation.Token, teacher.Token);
        Assert.Empty(store.Evidence);
        Assert.Empty(store.Skills);
        Assert.Null(lesson.SkillId);
        Assert.Null(lesson.Answer);
        Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
    }

    [Fact]
    public async Task PracticeAsync_CanceledGeneration_DoesNotCreateLessonOrChangeEvidence()
    {
        var store = new KnowledgeStore();
        var skill = Skill.Create(new UserId(1), "Topic");
        store.Add(skill);
        var lessons = new Lessons(null);
        using var cancellation = new CancellationTokenSource();
        var service = new LearningService(lessons, new Teacher { Cancellation = cancellation, Failure = "cancel" },
            new KnowledgeService(store, new Clock()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PracticeAsync(new UserId(1), skill.Id, cancellation.Token));
        Assert.Null(lessons.Added);
        Assert.Empty(store.Evidence);
        Assert.Single(store.Skills);
    }

    [Fact]
    public async Task StatusAsync_CanceledRequest_PropagatesCancellation()
    {
        var service = new KnowledgeService(new KnowledgeStore(), new Clock());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StatusAsync(new UserId(1), Guid.NewGuid(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListAsync(new UserId(1), cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => service.StatusAsync(default, Guid.NewGuid(), CancellationToken.None));
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    private sealed class Lessons(Lesson? active) : ILessonStore
    {
        public Lesson? Added { get; private set; }
        public Task<Lesson?> FindActiveAsync(UserId user, CancellationToken token) => Task.FromResult(active);
        public void Add(Lesson lesson) => Added = lesson;
    }

    private sealed class Teacher : ILessonTeacher
    {
        public string Failure { get; init; } = "";
        public CancellationTokenSource? Cancellation { get; init; }
        public CancellationToken Token { get; private set; }
        public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token)
        {
            Token = token;
            if (Failure == "cancel") Cancellation!.Cancel();
            return Task.FromResult(new LessonMaterial("Theory", "Question"));
        }
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token)
        {
            Token = token;
            if (Failure == "cancel") Cancellation!.Cancel();
            if (Failure == "provider failure") throw new LessonProviderException();
            return Task.FromResult(new LessonAssessment(Failure == "invalid feedback" ? "" : "Feedback", true)
            {
                Source = Failure == "invalid source" ? new string('s', 201) : "test-model"
            });
        }
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
