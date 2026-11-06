using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class ReviewFlowTests
{
    [Theory]
    [InlineData("review")]
    [InlineData("later")]
    [InlineData("skip")]
    [InlineData("list")]
    public async Task HandleAsync_CanceledReviewCommand_DoesNotGenerateOrChangeDeadline(string action)
    {
        var (knowledge, skill, schedule) = Setup();
        var teacher = new Teacher();
        var service = new LearningService(new Lessons(), teacher, knowledge);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var deadline = schedule.DueAt;
        if (action == "list")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => knowledge.ReviewsAsync(skill.UserId, cancellation.Token));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action == "review"
                ? service.ReviewAsync(skill.UserId, skill.Id, cancellation.Token)
                : service.DeferReviewAsync(skill.UserId, skill.Id, action == "skip" ? ReviewDeferral.Skip : ReviewDeferral.Postpone, cancellation.Token));
        Assert.Equal(deadline, schedule.DueAt);
        Assert.Null(teacher.Request);
    }

    [Fact]
    public async Task ReviewAsync_GenerationCancelsCaller_LeavesDueReviewAndNoLesson()
    {
        var (knowledge, skill, schedule) = Setup();
        using var cancellation = new CancellationTokenSource();
        var teacher = new Teacher { Cancel = cancellation };
        var lessons = new Lessons();
        var service = new LearningService(lessons, teacher, knowledge);
        var deadline = schedule.DueAt;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReviewAsync(skill.UserId, skill.Id, cancellation.Token));
        Assert.Equal(cancellation.Token, teacher.Token);
        Assert.True(teacher.Request!.IsReview);
        Assert.Equal(deadline, schedule.DueAt);
        Assert.Null(lessons.Added);
    }

    [Fact]
    public async Task ReviewAsync_PausedLesson_ResumesExistingStepWithoutGenerationOrRescheduling()
    {
        var (knowledge, skill, schedule) = Setup();
        var lesson = Lesson.Start(skill.UserId, "Other topic", "Theory", "Question");
        lesson.Pause();
        var teacher = new Teacher();
        var service = new LearningService(new Lessons { Active = lesson }, teacher, knowledge);
        var result = Assert.IsType<LearningResult.Session>(await service.ReviewAsync(skill.UserId, skill.Id, CancellationToken.None));
        Assert.Equal(LessonStatus.Paused, result.Status);
        Assert.Equal(lesson.Topic, result.Topic);
        Assert.Null(teacher.Request);
        Assert.True(knowledge.IsDue(schedule));
    }

    [Fact]
    public async Task ReviewsAsync_MultipleOwnersAndPages_ListsEachDueSkillOnceInDeadlineOrder()
    {
        var store = new KnowledgeStore();
        for (var i = 0; i < 13; i++)
        {
            var skill = Skill.Create(new UserId(i == 12 ? 2 : 1), "Skill " + i);
            store.Add(skill);
            store.Add(ReviewSchedule.FromAnswer(ReviewScheduleTests.Answer(skill,
                DateTimeOffset.UnixEpoch.AddDays(-20 + i), true)));
        }
        var knowledge = new KnowledgeService(store, new Clock());
        var first = await knowledge.ReviewsAsync(new UserId(1), CancellationToken.None);
        var second = await knowledge.ReviewsAsync(new UserId(1), CancellationToken.None, 2);
        Assert.True(first.HasNext);
        Assert.Equal(10, first.Items.Count);
        Assert.False(second.HasNext);
        Assert.Equal(2, second.Items.Count);
        Assert.Equal(12, first.Items.Concat(second.Items).Select(i => i.Skill.Id).Distinct().Count());
        Assert.Equal(store.Reviews.Take(12).Select(r => r.SkillId), first.Items.Concat(second.Items).Select(i => i.Skill.Id));
        Assert.All(first.Items.Concat(second.Items), i => Assert.Equal(new UserId(1), i.Skill.UserId));
        Assert.Equal(first.Items, (await knowledge.ReviewsAsync(new UserId(1), CancellationToken.None)).Items);
    }

    private static (KnowledgeService Knowledge, Skill Skill, ReviewSchedule Schedule) Setup()
    {
        var store = new KnowledgeStore();
        var skill = Skill.Create(new UserId(1), "Fractions");
        store.Add(skill);
        var schedule = ReviewSchedule.FromAnswer(ReviewScheduleTests.Answer(skill, DateTimeOffset.UnixEpoch.AddDays(-1), true));
        store.Add(schedule);
        return (new KnowledgeService(store, new Clock()), skill, schedule);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    private sealed class Lessons : ILessonStore
    {
        public Lesson? Active { get; init; }
        public Lesson? Added { get; private set; }
        public Task<Lesson?> FindActiveAsync(UserId user, CancellationToken token) => Task.FromResult(Active);
        public void Add(Lesson lesson) => Added = lesson;
    }

    private sealed class Teacher : ILessonTeacher
    {
        public CancellationTokenSource? Cancel { get; init; }
        public CancellationToken Token { get; private set; }
        public LessonRequest? Request { get; private set; }
        public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token)
        {
            Request = request;
            Token = token;
            Cancel?.Cancel();
            return Task.FromResult(new LessonMaterial("Context", "New question"));
        }
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token) => throw new NotSupportedException();
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
