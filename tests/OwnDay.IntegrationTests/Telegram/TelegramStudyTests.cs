using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Delivery;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramStudyTests
{
    [Theory]
    [InlineData("failure")]
    [InlineData("invalid")]
    public async Task Post_InvitationProviderFailure_PreservesInvitationForExplicitRetry(string failure)
    {
        var clock = new Clock();
        var teacher = new TelegramReviewTests.Teacher { Failure = failure };
        using var factory = new OwnDayHostFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            Configure(services, clock, new());
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
        }));
        using var client = host.CreateClient();
        await Post(client, 1, 101, "/learn_program Algebra | Beginner");
        await Post(client, 2, 101, "/learn_schedule Europe/Moscow | 1 | 18:00-20:00");
        var invitation = await Dispatch(host.Services);
        await Post(client, 3, 101, $"/learn_invite_start {invitation.InvitationId:N}");
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            Assert.Empty(await db.Set<Lesson>().ToListAsync());
            Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
            Assert.Equal(invitation.InvitationId, (await db.Set<StudySchedule>().SingleAsync()).InvitationId);
        }
        teacher.Failure = "";
        await Post(client, 4, 101, $"/learn_invite_start {invitation.InvitationId:N}");
        using var inspect = host.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Null((await stored.Set<StudySchedule>().SingleAsync()).InvitationId);
        Assert.Equal(LessonStatus.AwaitingAnswer, (await stored.Set<Lesson>().SingleAsync()).Status);
    }

    [Fact]
    public async Task StartAsync_ProviderCancelsCaller_PreservesInvitationWithoutCreatingLesson()
    {
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        var teacher = new TelegramCurriculumTests.Teacher { CancelAfterGenerate = cancellation };
        using var factory = new OwnDayHostFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, clock, teacher)));
        using var client = host.CreateClient();
        await Post(client, 1, 101, "/learn_program Algebra | Beginner");
        await Post(client, 2, 101, "/learn_schedule Europe/Moscow | 1 | 18:00-20:00");
        var invitation = await Dispatch(host.Services);
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<StudyService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(new(101), invitation.InvitationId!.Value, cancellation.Token));
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(cancellation.Token, teacher.ReceivedToken);
        Assert.Equal(invitation.InvitationId, (await db.Set<StudySchedule>().SingleAsync()).InvitationId);
        Assert.Empty(await db.Set<Lesson>().ToListAsync());
    }

    [Fact]
    public async Task Post_ConfigureInviteLaterSkipAndStart_DeduplicatesAndPreservesProgress()
    {
        var clock = new Clock();
        var teacher = new TelegramCurriculumTests.Teacher();
        using var factory = new OwnDayHostFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, clock, teacher)));
        using var client = host.CreateClient();
        await Post(client, 1, 101, "/learn_program algebra | beginner");
        await Post(client, 2, 101, "/learn_schedule Europe/Moscow | 1 | 18:00-20:00");
        await Post(client, 2, 101, "/learn_schedule UTC | 30 | 01:00-02:00");
        var first = await Dispatch(host.Services);
        Assert.Equal(clock.Now.AddDays(1), first.NextAt);
        var invitation = first.InvitationId!.Value;
        await Dispatch(host.Services);
        await Post(client, 3, 202, $"/learn_invite_start {invitation:N}");
        await Post(client, 4, 101, $"/learn_invite_later {invitation:N}");
        await Post(client, 5, 101, $"/learn_invite_later {invitation:N}");
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            Assert.Equal(clock.Now.AddHours(1), (await db.Set<StudySchedule>().SingleAsync()).NextAt);
            Assert.Empty(await db.Set<Lesson>().ToListAsync());
            Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
            Assert.Empty(await db.Set<ReviewSchedule>().ToListAsync());
            Assert.Equal(1, await db.TelegramOutboxMessages.CountAsync(m => m.StudyInvitationId != null));
        }
        clock.Now = clock.Now.AddHours(1);
        var later = await Dispatch(host.Services);
        Assert.NotEqual(invitation, later.InvitationId);
        await Post(client, 6, 101, $"/learn_invite_skip {later.InvitationId:N}");
        await Post(client, 6, 101, $"/learn_invite_skip {later.InvitationId:N}");
        clock.Now = clock.Now.AddDays(1).AddHours(-1);
        var next = await Dispatch(host.Services);
        await Post(client, 7, 101, $"/learn_invite_start {next.InvitationId:N}");
        await Post(client, 7, 101, $"/learn_invite_start {next.InvitationId:N}");
        await Post(client, 8, 101, $"/learn_invite_start {next.InvitationId:N}");
        using var inspect = host.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(LessonStatus.AwaitingAnswer, (await stored.Set<Lesson>().SingleAsync()).Status);
        Assert.Equal(1, teacher.Generated);
        Assert.Empty(await stored.Set<LearningEvidence>().ToListAsync());
        Assert.Empty(await stored.Actions.ToListAsync());
        Assert.Empty(await stored.Projects.ToListAsync());
        Assert.Equal(8, await stored.ProcessedTelegramUpdates.CountAsync());
        Assert.Equal(3, await stored.TelegramOutboxMessages.CountAsync(m => m.StudyInvitationId != null));
    }

    [Fact]
    public async Task Dispatch_DueReviewsTakePriorityAndSkip_DoesNotRescheduleOrAssessSkill()
    {
        var clock = new Clock();
        using var factory = new OwnDayHostFactory();
        var teacher = new TelegramCurriculumTests.Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, clock, teacher)));
        using var client = host.CreateClient();
        await Post(client, 1, 101, "/learn algebra");
        await Post(client, 2, 101, "/answer correct");
        await Post(client, 3, 101, "/learn_program physics | beginner");
        await Post(client, 4, 101, "/learn_schedule Europe/Moscow | 1 | 18:00-20:00");
        clock.Now = clock.Now.AddDays(30);
        var invitation = await Dispatch(host.Services);
        Assert.NotNull(invitation.SkillId);
        Assert.Null(invitation.ProgramId);
        await Post(client, 5, 101, $"/learn_invite_skip {invitation.InvitationId:N}");
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            Assert.Equal(clock.Now.AddDays(-29), (await db.Set<ReviewSchedule>().SingleAsync()).DueAt);
            Assert.Equal(1, await db.Set<LearningEvidence>().CountAsync());
            Assert.Equal(1, await db.Set<Lesson>().CountAsync());
        }
        clock.Now = clock.Now.AddDays(1);
        invitation = await Dispatch(host.Services);
        await Post(client, 6, 101, $"/learn_invite_start {invitation.InvitationId:N}");
        Assert.True(teacher.Requests.Last().IsReview);
        using var inspect = host.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var review = await stored.Set<Lesson>().SingleAsync(l => l.IsReview);
        Assert.Equal(invitation.SkillId, review.SkillId);
        Assert.Equal(1, await stored.Set<LearningEvidence>().CountAsync());
    }

    [Theory]
    [InlineData("/learn_schedule missing/timezone | 1 | 18:00-20:00")]
    [InlineData("/learn_schedule UTC | 0 | 18:00-20:00")]
    [InlineData("/learn_schedule UTC | 31 | 18:00-20:00")]
    [InlineData("/learn_schedule UTC | 1 | 20:00-18:00")]
    [InlineData("/learn_schedule UTC | 1 | 09:00-11:00,10:00-12:00")]
    [InlineData("/learn_schedule UTC | 1 | 99:00-20:00")]
    [InlineData("/learn_notify_pause extra")]
    public async Task Post_InvalidSettings_DoesNotCreateSchedule(string command)
    {
        using var host = new OwnDayHostFactory();
        using var client = host.CreateClient();
        await Post(client, 1, 101, command);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.Set<StudySchedule>().ToListAsync());
        Assert.Contains("/learn_schedule", (await db.TelegramOutboxMessages.SingleAsync()).Text);
    }

    [Fact]
    public async Task Dispatch_PausedNotificationsOrActiveLesson_DoesNotEnqueueInvitation()
    {
        var clock = new Clock();
        using var factory = new OwnDayHostFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, clock, new())));
        using var client = host.CreateClient();
        await Post(client, 1, 101, "/learn_program algebra | beginner");
        await Post(client, 2, 101, "/learn_schedule Europe/Moscow | 1 | 18:00-20:00");
        await Post(client, 3, 101, "/learn_notify_pause");
        Assert.Null((await Dispatch(host.Services)).InvitationId);
        await Post(client, 4, 101, "/learn_notify_resume");
        await Post(client, 5, 101, "/learn_next");
        await Post(client, 6, 101, "/learn_pause");
        Assert.Null((await Dispatch(host.Services)).InvitationId);
        using var scope = host.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>()
            .TelegramOutboxMessages.CountAsync(m => m.StudyInvitationId != null));
    }

    [Theory]
    [InlineData("configure")]
    [InlineData("plan")]
    [InlineData("pause")]
    [InlineData("start")]
    [InlineData("later")]
    public async Task Handle_CanceledCaller_DoesNotChangeSchedule(string action)
    {
        using var host = new OwnDayHostFactory();
        _ = host.CreateClient();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<StudyService>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (action)
            {
                case "configure": await service.ConfigureAsync(new(101), "UTC", 1, [new(new(9, 0), new(10, 0))], token); break;
                case "plan": await service.PlanAsync(new(101), token); break;
                case "pause": await service.PauseAsync(new(101), true, token); break;
                case "start": await service.StartAsync(new(101), Guid.NewGuid(), token); break;
                default: await service.DismissAsync(new(101), Guid.NewGuid(), true, token); break;
            }
        });
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().Set<StudySchedule>().ToListAsync());
    }

    internal static void Configure(IServiceCollection services, Clock clock, TelegramCurriculumTests.Teacher teacher)
    {
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
        services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(new TelegramCurriculumTests.Planner { NoQuestion = true }));
        services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
    }

    internal static async Task<StudySchedule> Dispatch(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<StudyInvitationDispatcher>().DispatchBatchAsync(CancellationToken.None);
        return await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().Set<StudySchedule>().AsNoTracking().SingleAsync();
    }

    internal static Task Post(HttpClient client, int id, long user, string text) => TelegramCurriculumTests.PostAsync(client, id, user, text);

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 15, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
