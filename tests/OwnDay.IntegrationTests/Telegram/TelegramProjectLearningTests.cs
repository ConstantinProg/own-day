using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Actions;
using OwnDay.Application.Learning;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Actions;
using OwnDay.Domain.Learning;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramProjectLearningTests
{
    [Fact]
    public async Task Post_ProjectPracticeAndAnswer_PreservesRealWorkAndPersistsEducationalEvidenceOnce()
    {
        using var factory = new OwnDayHostFactory();
        var teacher = new TelegramCurriculumTests.Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(new TelegramCurriculumTests.Planner { NoQuestion = true }));
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
        }));
        using var client = host.CreateClient();
        var (projectId, actionId) = await SeedAsync(host.Services, 101, "Build a website", "Calculate page size");
        await Post(client, 1, "/learn_project " + projectId + " | Beginner");
        var sharedSkill = Skill.Create(new(101), "Arithmetic", "Use arithmetic to calculate sizes");
        using (var seed = host.Services.CreateScope())
        {
            var dbContext = seed.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            dbContext.Add(sharedSkill);
            await dbContext.SaveChangesAsync();
        }
        await Post(client, 2, $"/learn_link basics | {sharedSkill.Id} | I confirm the same arithmetic skill");
        await Post(client, 3, "/learn_project_report");
        await Post(client, 4, $"/learn_project_practice basics | {actionId}");
        await Post(client, 4, $"/learn_project_practice basics | {actionId}");
        await Post(client, 5, "/learn_pause");
        await Post(client, 6, "/learn_resume");
        await Post(client, 7, "/answer correct");
        await Post(client, 7, "/answer correct");
        await Post(client, 8, "/learn_project_report");
        var request = Assert.Single(teacher.Requests);
        Assert.Equal(new ProjectPracticeContext(projectId, "Build a website", actionId, "Calculate page size"), request.ProjectPractice);
        Assert.Equal(TeachingAction.Practice, request.Teaching!.Action);
        Assert.Equal(sharedSkill.Description, request.SharedSkill!.Description);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var lesson = await db.Set<Lesson>().SingleAsync();
        Assert.Equal(actionId, lesson.ProjectActionId);
        Assert.Equal(sharedSkill.Id, lesson.SkillId);
        Assert.Equal("basics", lesson.TopicCode);
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.Equal(lesson.Id, (await db.Set<LearningEvidence>().SingleAsync()).LessonId);
        Assert.Single(await db.Set<ReviewSchedule>().ToListAsync());
        Assert.Single((await db.Set<LearningProgram>().Include(p => p.Routes).SingleAsync()).Routes);
        var action = await db.Actions.SingleAsync();
        Assert.Equal(ActionStatus.Active, action.Status);
        Assert.Null(action.CompletedAt);
        Assert.Equal("Calculate page size", action.Title);
        Assert.Equal(ProjectStatus.Active, (await db.Projects.SingleAsync()).Status);
        Assert.Equal(8, await db.ProcessedTelegramUpdates.CountAsync());
        var replies = await db.TelegramOutboxMessages.ToListAsync();
        Assert.Contains(replies, r => r.Text.Contains("1 positive, 0 negative", StringComparison.Ordinal));
        Assert.Contains(replies, r => r.Text.Contains("No Commitments are created", StringComparison.Ordinal));
        Assert.Contains(replies, r => r.Text.Contains($"task #{actionId}", StringComparison.Ordinal));
        Assert.All(replies, r => Assert.InRange(r.Text.Length, 1, 4096));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("otherProject")]
    [InlineData("standalone")]
    [InlineData("completedTask")]
    [InlineData("completedProject")]
    [InlineData("missing")]
    [InlineData("prerequisite")]
    [InlineData("diagnosis")]
    [InlineData("activeLesson")]
    public async Task Post_IneligibleProjectPractice_RejectsWithoutGenerationOrWorkMutation(string scenario)
    {
        using var factory = new OwnDayHostFactory();
        var teacher = new TelegramCurriculumTests.Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(new TelegramCurriculumTests.Planner { NoQuestion = scenario != "diagnosis" }));
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
        }));
        using var client = host.CreateClient();
        var (projectId, actionId) = await SeedAsync(host.Services, 101, "Private project", "Private task");
        await Post(client, 1, $"/learn_project {projectId} | Beginner");
        if (scenario is "foreign" or "otherProject")
        {
            var other = await SeedAsync(host.Services, scenario == "foreign" ? 202 : 101, "Other project", "Other task");
            actionId = other.ActionId;
        }
        using (var setup = host.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            if (scenario == "completedTask") (await db.Actions.SingleAsync()).Complete(DateTime.UtcNow);
            if (scenario == "completedProject") (await db.Projects.SingleAsync()).Complete(DateTime.UtcNow);
            if (scenario == "standalone")
                actionId = (await setup.ServiceProvider.GetRequiredService<ActionService>().AddAsync(new(101), "Unlinked", CancellationToken.None)).Id;
            if (scenario == "activeLesson") db.Add(Lesson.Start(new(101), "Existing", "Theory", "Question"));
            await db.SaveChangesAsync();
        }
        if (scenario == "missing") actionId = long.MaxValue;
        HashSet<Guid> earlierReplies;
        using (var before = host.Services.CreateScope())
            earlierReplies = (await before.ServiceProvider.GetRequiredService<OwnDayDbContext>().TelegramOutboxMessages
                .Select(m => m.Id).ToListAsync()).ToHashSet();
        await Post(client, 2, $"/learn_project_practice {(scenario == "prerequisite" ? "advanced" : "basics")} | {actionId}");
        Assert.Empty(teacher.Requests);
        using var scope = host.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await database.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(scenario == "activeLesson" ? 1 : 0, await database.Set<Lesson>().CountAsync());
        Assert.All(await database.Actions.ToListAsync(), a => Assert.Equal(scenario == "completedTask" ? ActionStatus.Completed : ActionStatus.Active, a.Status));
        var result = (await database.TelegramOutboxMessages.ToListAsync()).Where(m => !earlierReplies.Contains(m.Id)).ToArray();
        Assert.NotEmpty(result);
        Assert.DoesNotContain(result, r => r.Text.Contains("Other task", StringComparison.Ordinal));
        var expected = scenario switch
        {
            "completedProject" => "project is completed",
            "prerequisite" => "requires completed prerequisites",
            "diagnosis" => "Optional diagnostic",
            "activeLesson" => "Answer or finish",
            _ => "Active task not found"
        };
        Assert.Contains(result, r => r.Text.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_ProjectReport_PaginatesOnlyOwnedProjectTasksAndDoesNotLeakToOtherUsers()
    {
        using var factory = new OwnDayHostFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(new TelegramCurriculumTests.Planner { NoQuestion = true }))));
        using var client = host.CreateClient();
        var (projectId, _) = await SeedAsync(host.Services, 101, new string('p', 200), "Task 0");
        using (var seed = host.Services.CreateScope())
        {
            var actions = seed.ServiceProvider.GetRequiredService<ActionService>();
            for (var i = 1; i <= 10; i++) await actions.AddAsync(new(101), new string('t', 195) + i, projectId, CancellationToken.None);
            await actions.AddAsync(new(101), "Not linked", CancellationToken.None);
        }
        await SeedAsync(host.Services, 202, "Secret project", "Secret task");
        await Post(client, 1, $"/learn_project {projectId} | Beginner");
        await Post(client, 2, "/learn_project_report");
        await Post(client, 3, "/learn_project_report 2");
        await TelegramCurriculumTests.PostAsync(client, 4, 202, "/learn_project_report");
        await TelegramCurriculumTests.PostAsync(client, 5, 202, "/learn_project_practice basics | 1");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var replies = await db.TelegramOutboxMessages.ToListAsync();
        var service = scope.ServiceProvider.GetRequiredService<CurriculumService>();
        var first = Assert.IsType<CurriculumResult.ProjectReport>(await service.ProjectReportAsync(new(101), 1, CancellationToken.None)).Value;
        var second = Assert.IsType<CurriculumResult.ProjectReport>(await service.ProjectReportAsync(new(101), 2, CancellationToken.None)).Value;
        Assert.Equal(10, first.Actions.Count);
        Assert.True(first.HasNext);
        Assert.Single(second.Actions);
        Assert.False(second.HasNext);
        Assert.DoesNotContain(second.Actions[0].Id, first.Actions.Select(a => a.Id));
        Assert.Contains(replies, r => r.Text.Contains("/learn_project_report 2", StringComparison.Ordinal));
        Assert.All(replies.Where(r => r.ChatId == 202), r => Assert.DoesNotContain(new string('p', 200), r.Text));
        Assert.DoesNotContain(replies, r => r.Text.Contains("Secret task", StringComparison.Ordinal) || r.Text.Contains("Not linked", StringComparison.Ordinal));
        Assert.All(replies, r => Assert.InRange(r.Text.Length, 1, 4096));
        Assert.Empty(await db.Set<Skill>().ToListAsync());
    }

    [Theory]
    [InlineData("/learn_project_report 0")]
    [InlineData("/learn_project_report 2147483647")]
    [InlineData("/learn_project_report extra")]
    [InlineData("/learn_project_practice basics")]
    [InlineData("/learn_project_practice basics | 0")]
    [InlineData("/learn_project_practice basics | wrong")]
    public async Task Post_MalformedProjectCommand_ShowsUsage(string command)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await Post(client, 1, command);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), r => r.Text.Contains("/learn_project_practice", StringComparison.Ordinal));
        Assert.Empty(await db.Set<Lesson>().ToListAsync());
    }

    [Fact]
    public async Task ProjectPracticeAsync_CancellationAfterGeneration_DoesNotMutateTrackedState()
    {
        using var factory = new OwnDayHostFactory();
        using var cancellation = new CancellationTokenSource();
        var teacher = new TelegramCurriculumTests.Teacher { CancelAfterGenerate = cancellation };
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(new TelegramCurriculumTests.Planner { NoQuestion = true }));
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
        }));
        using var client = host.CreateClient();
        var (projectId, actionId) = await SeedAsync(host.Services, 101, "Project", "Task");
        await Post(client, 1, $"/learn_project {projectId} | Beginner");
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CurriculumService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProjectPracticeAsync(new(101), "basics", actionId, cancellation.Token));
        Assert.Equal(cancellation.Token, teacher.ReceivedToken);
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_ProjectTeacherFailureOrInvalidMaterial_DoesNotCreatePartialLearningState(bool invalidMaterial)
    {
        using var factory = new OwnDayHostFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(new TelegramCurriculumTests.Planner { NoQuestion = true }));
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(new FailingTeacher(invalidMaterial)));
        }));
        using var client = host.CreateClient();
        var (projectId, actionId) = await SeedAsync(host.Services, 101, "Project", "Task");
        await Post(client, 1, $"/learn_project {projectId} | Beginner");
        await Post(client, 2, $"/learn_project_practice basics | {actionId}");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.Set<Lesson>().ToListAsync());
        Assert.Empty(await db.Set<Skill>().ToListAsync());
        Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(ActionStatus.Active, (await db.Actions.SingleAsync()).Status);
        Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), m => m.Text.Contains("temporarily unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_UnlinkedProgram_ExplainsMissingProjectAndDoesNotInventOne()
    {
        using var factory = new OwnDayHostFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(new TelegramCurriculumTests.Planner { NoQuestion = true }))));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_program Math | Beginner");
        await Post(client, 2, "/learn_project_report");
        await Post(client, 3, "/learn_project_practice basics | 1");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(2, await db.TelegramOutboxMessages.CountAsync(m => m.Text.Contains("no project link")));
        Assert.Empty(await db.Projects.ToListAsync());
        Assert.Empty(await db.Actions.ToListAsync());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var service = scope.ServiceProvider.GetRequiredService<CurriculumService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProjectReportAsync(new(101), 1, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProjectPracticeAsync(new(101), "basics", 1, canceled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ProjectReportAsync(default, 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ProjectPracticeAsync(default, "basics", 1, CancellationToken.None));
    }

    private sealed class FailingTeacher(bool invalidMaterial) : ILessonTeacher
    {
        public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token) => invalidMaterial
            ? Task.FromResult(new LessonMaterial("", "Question")) : throw new LessonProviderException();
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token) => throw new NotSupportedException();
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) => throw new NotSupportedException();
    }

    internal static async Task<(long ProjectId, long ActionId)> SeedAsync(IServiceProvider services, long user, string title, string task)
    {
        using var scope = services.CreateScope();
        var project = await scope.ServiceProvider.GetRequiredService<StructuredItemService>().CreateProjectAsync(new(user), title, CancellationToken.None);
        var action = await scope.ServiceProvider.GetRequiredService<ActionService>().AddAsync(new(user), task, project.Id, CancellationToken.None);
        return (project.Id, action.Id);
    }

    private static Task Post(HttpClient client, int id, string command) => TelegramCurriculumTests.PostAsync(client, id, 101, command);
}
