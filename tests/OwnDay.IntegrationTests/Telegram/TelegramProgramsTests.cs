using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramProgramsTests
{
    [Fact]
    public async Task Post_SharedSkillWithRepeatedDifficulty_AdaptsNewProgramWithoutTransferringProgress()
    {
        using var factory = new OwnDayHostFactory();
        var planner = new TelegramCurriculumTests.Planner { NoQuestion = true };
        var teachingPlanner = new TelegramAdaptiveTeachingTests.Planner();
        var teacher = new TelegramCurriculumTests.Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            Configure(services, planner, teacher);
            services.Replace(ServiceDescriptor.Singleton<ITeachingPlanner>(teachingPlanner));
        }));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_program Algebra | Beginner");
        await Post(client, 2, "/learn_next");
        await Post(client, 3, "/answer wrong");
        await Post(client, 4, "/learn_next");
        await Post(client, 5, "/answer wrong");
        Assert.Empty(teachingPlanner.Requests);
        Guid skillId;
        using (var scope = host.Services.CreateScope())
            skillId = (await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().Set<Skill>().SingleAsync()).Id;
        planner.DifferentDiscipline = true;
        await Post(client, 6, "/learn_program Physics | Beginner");
        await Post(client, 7, $"/learn_link basics | {skillId} | The same algebraic concept");
        await Post(client, 8, "/learn_next");

        var adaptation = Assert.Single(teachingPlanner.Requests);
        Assert.Equal("Physics", adaptation.Discipline);
        Assert.Equal(2, adaptation.State.NegativeAssessments);
        Assert.Empty(adaptation.Completed);
        var lesson = teacher.Requests[^1];
        Assert.Equal(TeachingAction.Simplify, Assert.IsType<TeachingContext>(lesson.Teaching).Action);
        Assert.Equal(2, Assert.IsType<SharedSkillContext>(lesson.SharedSkill).State.NegativeAssessments);
        using var inspection = host.Services.CreateScope();
        var db = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var programs = await db.Set<LearningProgram>().Include(p => p.Routes).ToListAsync();
        Assert.Single(programs.Single(p => p.Goal == "Algebra").Routes);
        var target = programs.Single(p => p.Goal == "Physics");
        Assert.Equal(2, target.CurrentRoute.Version);
        Assert.Equal(1, target.CurrentRoute.ProgressRootVersion);
        Assert.Equal(skillId, (await db.Set<Lesson>().SingleAsync(l => l.ProgramId == target.Id)).SkillId);
        Assert.Equal(2, await db.Set<LearningEvidence>().CountAsync());
    }

    [Fact]
    public async Task Post_ProgramPages_ListsOnlyOwnedProgramsWithStableNavigation()
    {
        using var host = new OwnDayHostFactory();
        using var client = host.CreateClient();
        Guid[] expected;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            for (var i = 0; i < 11; i++)
                db.Add(LearningProgram.Create(new UserId(101), $"Goal {i}", "Beginner", null, "Discipline", [new("a", "A", [])], null));
            db.Add(LearningProgram.Create(new UserId(202), "Private", "Beginner", null, "Discipline", [new("a", "A", [])], null));
            await db.SaveChangesAsync();
            expected = await db.Set<LearningProgram>().Where(p => p.UserId == new UserId(101)).OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync();
        }
        await Post(client, 1, "/learn_programs");
        List<TelegramOutboxMessage> firstMessages;
        using (var scope = host.Services.CreateScope())
            firstMessages = await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().TelegramOutboxMessages.ToListAsync();
        await Post(client, 2, "/learn_programs 2");
        using var inspection = host.Services.CreateScope();
        var database = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var firstIds = firstMessages.Select(m => m.Id).ToArray();
        var first = firstMessages.Select(m => m.Text).ToList();
        var second = await database.TelegramOutboxMessages.Where(m => !firstIds.Contains(m.Id)).Select(m => m.Text).ToListAsync();
        Assert.Equal(11, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Contains("Next: /learn_programs 2", first);
        Assert.Contains("Previous: /learn_programs 1", second);
        for (var i = 0; i < 10; i++) Assert.Contains(first, message => message.Contains(expected[i].ToString(), StringComparison.Ordinal));
        Assert.Contains(second, message => message.Contains(expected[10].ToString(), StringComparison.Ordinal));
        Assert.All(first.Concat(second), m => Assert.DoesNotContain("Private", m));
    }

    [Fact]
    public async Task Post_InvalidTopicConfirmation_PreservesSkillsAndDiagnosticState()
    {
        using var factory = new OwnDayHostFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services,
            new TelegramCurriculumTests.Planner(), new TelegramCurriculumTests.Teacher())));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_program Goal | Beginner");
        var skill = Skill.Create(new UserId(101), "Existing concept");
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            db.Add(skill);
            await db.SaveChangesAsync();
        }
        await Post(client, 2, $"/learn_link basics | {skill.Id} | Diagnosis is pending");
        await Post(client, 3, "/learn_diagnose skip");
        await Post(client, 4, $"/learn_link missing | {skill.Id} | Unknown topic");
        await Post(client, 5, $"/learn_link basics | {Guid.NewGuid()} | Missing skill");
        await Post(client, 6, $"/learn_link basics | {skill.Id} | {new string('r', 501)}");
        await Post(client, 7, $"/learn_link basics | {skill.Id} | ");
        using var inspection = host.Services.CreateScope();
        var database = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await database.Set<ProgramSkillLink>().ToListAsync());
        Assert.Single(await database.Set<Skill>().ToListAsync());
        Assert.Empty(await database.Set<LearningEvidence>().ToListAsync());
        Assert.Single(await database.Set<CurriculumRoute>().ToListAsync());
    }

    [Fact]
    public async Task HandleAsync_CanceledSecondCreationOrSelection_PreservesCurrentContext()
    {
        using var factory = new OwnDayHostFactory();
        var planner = new TelegramCurriculumTests.Planner { NoQuestion = true };
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, planner, new TelegramCurriculumTests.Teacher())));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_program Original | Beginner");
        using var inspection = host.Services.CreateScope();
        var db = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var original = await db.Set<LearningProgram>().SingleAsync();
        var service = inspection.ServiceProvider.GetRequiredService<CurriculumService>();
        using var canceled = new CancellationTokenSource();
        planner.CancelAfterPlan = canceled;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(new UserId(101), "New", "Beginner", null, canceled.Token));
        Assert.Equal(canceled.Token, planner.ReceivedToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SelectAsync(new UserId(101), original.Id, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LinkAsync(new UserId(101), "basics", Guid.NewGuid(), "Reason", canceled.Token));
        db.ChangeTracker.DetectChanges();
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified);
        Assert.Single(await db.Set<LearningProgram>().ToListAsync());
        Assert.Equal(original.Id, (await db.Set<LearningContext>().SingleAsync()).ProgramId);
    }

    [Fact]
    public async Task Post_ConfirmedSharedSkill_ReusesEvidenceAndScheduleWithIndependentProgramProgress()
    {
        using var factory = new OwnDayHostFactory();
        var planner = new TelegramCurriculumTests.Planner { NoQuestion = true };
        var teacher = new TelegramCurriculumTests.Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, planner, teacher)));
        using var client = host.CreateClient();
        var update = 0;
        Task Post(string text) => TelegramCurriculumTests.PostAsync(client, ++update, 101, text);
        await Post("/learn_program algebra | Beginner");
        await Post("/learn_next");
        await Post("/answer correct");
        await Post("/learn_focus basics");
        await Post("/answer correct");
        await Post("/learn_focus basics");
        await Post("/answer correct");
        Guid sourceId;
        Guid skillId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            sourceId = (await db.Set<LearningProgram>().SingleAsync()).Id;
            skillId = (await db.Set<Skill>().SingleAsync()).Id;
            Assert.Equal(SkillMastery.Demonstrated, LearnerSkillState.Evaluate(await db.Set<LearningEvidence>().ToListAsync()).Mastery);
        }
        planner.DifferentDiscipline = true;
        await Post("/learn_program physics | Beginner");
        await Post($"/learn_link basics | {skillId} | The same algebraic concept is required in physics");
        await TelegramCurriculumTests.PostAsync(client, update, 101, $"/learn_link basics | {skillId} | Duplicate update");
        await Post($"/learn_link basics | {skillId} | Repeated confirmation");
        await Post("/learn_links");
        await Post("/learn_next");
        Assert.Equal("Basics", teacher.Requests[^1].Topic);
        Assert.Equal("Physics", teacher.Requests[^1].Program!.Discipline);
        var shared = Assert.IsType<SharedSkillContext>(teacher.Requests[^1].SharedSkill);
        Assert.Equal(SkillMastery.Demonstrated, shared.State.Mastery);
        Assert.Contains("Mathematics", shared.Description);
        await Post("/answer wrong");
        await Post($"/learn_select {sourceId}");
        await Post("/learn_next");
        Assert.Equal("Advanced", teacher.Requests[^1].Topic);
        using var inspection = host.Services.CreateScope();
        var database = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var programs = await database.Set<LearningProgram>().Include(p => p.Routes).ToListAsync();
        Assert.Equal(2, programs.Count);
        Assert.Equal(3, programs.Single(p => p.Id == sourceId).Routes.Count);
        Assert.Single(programs.Single(p => p.Id != sourceId).Routes);
        var sourceEvidence = await database.Set<LearningEvidence>().Where(e => e.SkillId == skillId).ToListAsync();
        Assert.Equal(4, sourceEvidence.Count);
        Assert.Equal(SkillMastery.Conflicting, LearnerSkillState.Evaluate(sourceEvidence).Mastery);
        Assert.Single(await database.Set<ReviewSchedule>().ToListAsync());
        var link = Assert.Single(await database.Set<ProgramSkillLink>().ToListAsync());
        Assert.Equal("The same algebraic concept is required in physics", link.Reason);
        Assert.Equal(skillId, link.SkillId);
        var targetLesson = Assert.Single(await database.Set<Lesson>().Where(l => l.ProgramId == link.ProgramId).ToListAsync());
        Assert.Equal(skillId, targetLesson.SkillId);
        var curriculum = inspection.ServiceProvider.GetRequiredService<ICurriculumStore>();
        Assert.Empty(await curriculum.CompletedTopicsAsync(new UserId(101), link.ProgramId, 1, CancellationToken.None));
        Assert.Contains("basics", await curriculum.CompletedTopicsAsync(new UserId(101), sourceId, 3, CancellationToken.None));
        Assert.Equal(update, await database.ProcessedTelegramUpdates.CountAsync());
        var replies = await database.TelegramOutboxMessages.ToListAsync();
        Assert.Contains(replies, r => r.Text.Contains("UserConfirmedIdentity", StringComparison.Ordinal));
        Assert.All(replies, r => Assert.InRange(r.Text.Length, 1, 4096));
    }

    [Fact]
    public async Task Post_SimilarTitlesWithoutConfirmation_KeepsSkillsSeparateAndRejectsRelabeling()
    {
        using var factory = new OwnDayHostFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services,
            new TelegramCurriculumTests.Planner { NoQuestion = true }, new TelegramCurriculumTests.Teacher())));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_program algebra | Beginner");
        await Post(client, 2, "/learn_next");
        await Post(client, 3, "/answer correct");
        Guid sourceSkill;
        using (var scope = host.Services.CreateScope())
            sourceSkill = (await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().Set<Skill>().SingleAsync()).Id;
        await Post(client, 4, "/learn_program physics | Beginner");
        await Post(client, 5, "/learn_next");
        await Post(client, 6, "/answer correct");
        await Post(client, 7, $"/learn_link basics | {sourceSkill} | Similar title");
        using var inspection = host.Services.CreateScope();
        var db = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var skills = await db.Set<Skill>().ToListAsync();
        Assert.Equal(2, skills.Count);
        Assert.All(skills, s => Assert.Equal("Basics", s.Title));
        Assert.Empty(await db.Set<ProgramSkillLink>().ToListAsync());
        Assert.Equal(2, (await db.Set<LearningEvidence>().Select(e => e.SkillId).ToListAsync()).Distinct().Count());
        Assert.Equal(2, await db.Set<ReviewSchedule>().CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_UnfinishedLesson_BlocksProgramChangeUntilAnsweredOrFinished(bool paused)
    {
        using var factory = new OwnDayHostFactory();
        var planner = new TelegramCurriculumTests.Planner { NoQuestion = true };
        var teacher = new TelegramCurriculumTests.Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, planner, teacher)));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_program first | Beginner");
        Guid first;
        using (var scope = host.Services.CreateScope())
            first = (await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().Set<LearningProgram>().SingleAsync()).Id;
        await Post(client, 2, "/learn_program second | Beginner");
        await Post(client, 3, "/learn_next");
        if (paused) await Post(client, 4, "/learn_pause");
        await Post(client, 5, $"/learn_select {first}");
        await Post(client, 6, "/learn_program third | Beginner");
        Guid second;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            second = (await db.Set<LearningContext>().SingleAsync()).ProgramId;
            Assert.NotEqual(first, second);
            Assert.Equal(2, await db.Set<LearningProgram>().CountAsync());
            Assert.Equal(paused ? LessonStatus.Paused : LessonStatus.AwaitingAnswer, (await db.Set<Lesson>().SingleAsync()).Status);
            Assert.Equal(2, planner.Requests.Count);
        }
        await Post(client, 7, $"/learn_select {second}");
        await Post(client, 8, "/learn_finish");
        await Post(client, 9, $"/learn_select {first}");
        await Post(client, 10, "/learn_next");
        Assert.Equal("first", teacher.Requests[^1].Program!.Goal);
        using var inspection = host.Services.CreateScope();
        var database = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(first, (await database.Set<LearningContext>().SingleAsync()).ProgramId);
        Assert.Equal(2, await database.Set<Lesson>().CountAsync());
        Assert.Empty(await database.Set<LearningEvidence>().ToListAsync());
    }

    [Fact]
    public async Task Post_ForeignProgramsAndSkills_RejectsWithoutLeakingContext()
    {
        using var factory = new OwnDayHostFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services,
            new TelegramCurriculumTests.Planner { NoQuestion = true }, new TelegramCurriculumTests.Teacher())));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_program Private algebra | Beginner");
        await Post(client, 2, "/learn_next");
        await Post(client, 3, "/answer correct");
        Guid program;
        Guid skill;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            program = (await db.Set<LearningProgram>().SingleAsync()).Id;
            skill = (await db.Set<Skill>().SingleAsync()).Id;
        }
        await TelegramCurriculumTests.PostAsync(client, 4, 202, "/learn_program Public physics | Beginner");
        await TelegramCurriculumTests.PostAsync(client, 5, 202, $"/learn_select {program}");
        await TelegramCurriculumTests.PostAsync(client, 6, 202, $"/learn_link basics | {skill} | Foreign identity");
        await TelegramCurriculumTests.PostAsync(client, 7, 202, "/learn_programs");
        await TelegramCurriculumTests.PostAsync(client, 8, 202, "/learn_links");
        await TelegramCurriculumTests.PostAsync(client, 9, 202, "/learn_route");
        using var inspection = host.Services.CreateScope();
        var database = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await database.Set<ProgramSkillLink>().ToListAsync());
        Assert.NotEqual(program, (await database.Set<LearningContext>().SingleAsync(c => c.UserId == new UserId(202))).ProgramId);
        Assert.All(await database.TelegramOutboxMessages.Where(m => m.ChatId == 202).ToListAsync(), m => Assert.DoesNotContain("Private", m.Text));
    }

    [Fact]
    public async Task Post_FailedSecondProgram_PreservesSelectionAndHistory()
    {
        using var factory = new OwnDayHostFactory();
        var planner = new TelegramCurriculumTests.Planner { NoQuestion = true };
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, planner, new TelegramCurriculumTests.Teacher())));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_program Original | Beginner");
        planner.Failure = true;
        await Post(client, 2, "/learn_program New goal | Beginner");
        using var inspection = host.Services.CreateScope();
        var db = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var program = await db.Set<LearningProgram>().SingleAsync();
        Assert.Equal("Original", program.Goal);
        Assert.Equal(program.Id, (await db.Set<LearningContext>().SingleAsync()).ProgramId);
        Assert.Single(await db.Set<CurriculumRoute>().ToListAsync());
    }

    [Theory]
    [InlineData("/learn_programs 0")]
    [InlineData("/learn_programs 2147483647")]
    [InlineData("/learn_programs bad")]
    [InlineData("/learn_select bad")]
    [InlineData("/learn_link basics | bad | reason")]
    [InlineData("/learn_links extra")]
    public async Task Post_InvalidProgramCommand_ExplainsUsageWithoutMutation(string command)
    {
        using var host = new OwnDayHostFactory();
        using var client = host.CreateClient();
        await Post(client, 1, command);
        using var inspection = host.Services.CreateScope();
        var db = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.Set<LearningProgram>().ToListAsync());
        Assert.Empty(await db.Set<ProgramSkillLink>().ToListAsync());
        Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), m => m.Text.Contains("/learn_programs", StringComparison.Ordinal));
    }

    internal static void Configure(IServiceCollection services, TelegramCurriculumTests.Planner planner, TelegramCurriculumTests.Teacher teacher)
    {
        services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(planner));
        services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
    }

    private static Task Post(HttpClient client, int id, string text) => TelegramCurriculumTests.PostAsync(client, id, 101, text);
}
