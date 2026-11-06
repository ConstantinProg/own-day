using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramCurriculumTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Post_ProgramAcrossHostRestarts_RestoresDiagnosisVersionsAndNextTopic()
    {
        var connection = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required.");
        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new OwnDayDbContext(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connection).Options);
#pragma warning disable EF1003 // Schema name is generated from a GUID.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003
        try
        {
            var scopedConnection = connection + ";Search Path=" + schema;
            var options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(scopedConnection).Options;
            await using (var setup = new OwnDayDbContext(options))
            {
                // Upgrade a populated pre-program database; standalone lessons must stay intact.
                await setup.GetService<IMigrator>().MigrateAsync("20261105171329_RemoveLessonContextSummary");
                await setup.Database.ExecuteSqlRawAsync("""
                    INSERT INTO learning_lessons ("Id", "UserId", "Topic", "Theory", "Question", "Status")
                    VALUES ('11111111-1111-1111-1111-111111111111', 303, 'Legacy', 'Theory', 'Question', 0)
                    """);
                await setup.Database.MigrateAsync();
            }
            var planner = new Planner();
            var teacher = new Teacher();
            for (var restart = 0; restart < 3; restart++)
            {
                using var factory = new OwnDayHostFactory();
                using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<OwnDayDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<OwnDayDbContext>>();
                    services.AddDbContext<OwnDayDbContext>(builder => builder.UseNpgsql(scopedConnection));
                    services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(planner));
                    services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
                }));
                using var client = host.CreateClient();
                if (restart == 0)
                {
                    await PostAsync(client, 1, 101, "/project Build a website");
                    await PostAsync(client, 2, 101, "/learn_project 1 | Beginner");
                }
                else if (restart == 1)
                {
                    await PostAsync(client, 2, 101, "/learn_project 1 | Beginner");
                    await PostAsync(client, 3, 101, "/learn_route");
                    await PostAsync(client, 4, 101, "/learn_diagnose x = 2");
                    await PostAsync(client, 5, 101, "/learn_next");
                    await PostAsync(client, 6, 101, "/answer correct");
                }
                else
                {
                    await PostAsync(client, 4, 101, "/learn_diagnose x = 2");
                    await PostAsync(client, 7, 101, "/learn_next");
                    await PostAsync(client, 7, 101, "/learn_next");
                }
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
                var program = await db.Set<LearningProgram>().Include(p => p.Routes).SingleAsync();
                Assert.Equal("Beginner", program.Experience);
                Assert.Equal(1, program.ProjectId);
                Assert.Equal("Build a website", program.Goal);
                Assert.Equal(restart == 0, program.DiagnosticPending);
                Assert.Equal(restart == 0 ? 1 : 2, program.CurrentRoute.Version);
                Assert.Equal("basics", program.CurrentRoute.Topics[0].Code);
                Assert.Equal("basics", program.CurrentRoute.Topics[1].Prerequisites[0]);
                Assert.All(await db.TelegramOutboxMessages.ToListAsync(), m => Assert.InRange(m.Text.Length, 1, 4096));
                var legacy = await db.Set<Lesson>().SingleAsync(l => l.Topic == "Legacy");
                Assert.Null(legacy.ProgramId);
                Assert.Equal("Question", legacy.Question);
                if (restart == 2)
                {
                    Assert.Equal(2, program.Routes.Count);
                    Assert.Equal("x = 2", program.DiagnosticAnswer);
                    var active = await db.Set<Lesson>().SingleAsync(l => l.ProgramId == program.Id && l.Status == LessonStatus.AwaitingAnswer);
                    Assert.Equal("advanced", active.TopicCode);
                    Assert.Equal(2, active.RouteVersion);
                    Assert.Equal(7, await db.ProcessedTelegramUpdates.CountAsync());
                    Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), m => m.Text.Contains("Route v1", StringComparison.Ordinal));
                }
            }
            Assert.Equal(2, planner.Requests.Count);
            Assert.Equal(2, teacher.Generated);
            await using (var rollback = new OwnDayDbContext(options))
            {
                await rollback.GetService<IMigrator>().MigrateAsync("20261105171329_RemoveLessonContextSummary");
                var count = await rollback.Database.SqlQueryRaw<int>("""
                    SELECT count(*)::integer AS "Value" FROM learning_lessons
                    """).SingleAsync();
                Assert.Equal(3, count);
                await rollback.Database.MigrateAsync();
                Assert.Empty(await rollback.Set<LearningProgram>().ToListAsync());
                Assert.All(await rollback.Set<Lesson>().ToListAsync(), l =>
                {
                    Assert.Null(l.ProgramId);
                    Assert.Equal("Question", l.Question);
                });
            }
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("diagnose")]
    [InlineData("next")]
    public async Task HandleAsync_CanceledProviderResult_DoesNotMutateProgramOrCreateLesson(string action)
    {
        using var factory = new OwnDayHostFactory();
        var planner = new Planner();
        var teacher = new Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(planner));
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
        }));
        using var client = host.CreateClient();
        if (action != "create") await PostAsync(client, 1, 101, "/learn_program algebra | Beginner");
        if (action == "next") await PostAsync(client, 2, 101, "/learn_diagnose skip");
        using var cancellation = new CancellationTokenSource();
        planner.CancelAfterPlan = cancellation;
        teacher.CancelAfterGenerate = cancellation;
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CurriculumService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action switch
        {
            "create" => service.CreateAsync(new(101), "algebra", "Beginner", null, cancellation.Token),
            "diagnose" => service.DiagnoseAsync(new(101), "Answer", cancellation.Token),
            _ => service.NextAsync(new(101), cancellation.Token)
        });
        Assert.Equal(cancellation.Token, action == "next" ? teacher.ReceivedToken : planner.ReceivedToken);
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified);
        Assert.Empty(await db.Set<Lesson>().ToListAsync());
        if (action == "create") Assert.Empty(await db.Set<LearningProgram>().ToListAsync());
        else
        {
            var program = await db.Set<LearningProgram>().Include(p => p.Routes).SingleAsync();
            Assert.Null(program.DiagnosticAnswer);
            Assert.Single(program.Routes);
            Assert.Equal(action == "diagnose", program.DiagnosticPending);
        }
    }

    [Fact]
    public async Task Post_DiagnosisAndNextLesson_VersionsRouteAndRespectsDependencies()
    {
        using var factory = new OwnDayHostFactory();
        var planner = new Planner();
        var teacher = new Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(planner));
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
        }));
        using var client = host.CreateClient();
        await PostAsync(client, 1, 101, "/learn_program algebra | I know arithmetic");
        await PostAsync(client, 1, 101, "/learn_program algebra | I know arithmetic");
        await PostAsync(client, 2, 101, "/learn_next");
        Assert.Equal(0, teacher.Generated);
        await PostAsync(client, 3, 101, "/learn_diagnose x = 2");
        await PostAsync(client, 3, 101, "/learn_diagnose x = 2");
        await PostAsync(client, 4, 101, "/learn_route");
        await PostAsync(client, 5, 101, "/learn_next");
        await PostAsync(client, 5, 101, "/learn_next");
        await PostAsync(client, 6, 101, "/learn_next");
        Assert.Equal(1, teacher.Generated);
        await PostAsync(client, 7, 101, "/answer incorrect");
        await PostAsync(client, 8, 101, "/learn_next");
        await PostAsync(client, 9, 101, "/answer correct");
        await PostAsync(client, 10, 101, "/learn_next");
        await PostAsync(client, 11, 101, "/learn_finish");
        await PostAsync(client, 12, 101, "/learn_next");
        await PostAsync(client, 13, 101, "/answer correct");
        await PostAsync(client, 14, 101, "/learn_next");
        await PostAsync(client, 15, 202, "/learn_route");
        await PostAsync(client, 16, 202, "/learn_next");
        await PostAsync(client, 17, 202, "/learn_diagnose foreign answer");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var program = Assert.Single(await db.Set<LearningProgram>().Include(p => p.Routes).ToListAsync());
        Assert.Equal("algebra", program.Goal);
        Assert.Equal("I know arithmetic", program.Experience);
        Assert.Equal("x = 2", program.DiagnosticAnswer);
        Assert.False(program.DiagnosticPending);
        Assert.Equal(2, program.CurrentRoute.Version);
        Assert.Equal(2, program.Routes.Count);
        Assert.Equal(2, planner.Requests.Count);
        Assert.Equal("I know arithmetic", planner.Requests[0].Experience);
        Assert.Equal("x = 2", planner.Requests[1].Answer);
        Assert.Null(planner.Requests[0].Discipline);
        Assert.Equal("Mathematics", planner.Requests[1].Discipline);
        Assert.All(teacher.Requests, request =>
        {
            var context = Assert.IsType<LessonProgramContext>(request.Program);
            Assert.Equal("Mathematics", context.Discipline);
            Assert.Equal("algebra", context.Goal);
            Assert.Equal("I know arithmetic", context.Experience);
            Assert.Equal("Solve x + 2 = 4", context.DiagnosticQuestion);
            Assert.Equal("x = 2", context.DiagnosticAnswer);
            Assert.Contains(request.Topic, new[] { "Basics", "Advanced" });
        });
        Assert.Equal("Solve x + 2 = 4", planner.Requests[1].Question);
        var lessons = await db.Set<Lesson>().ToListAsync();
        Assert.Equal(4, lessons.Count);
        Assert.Equal(2, lessons.Count(l => l.TopicCode == "basics"));
        Assert.Equal(2, lessons.Count(l => l.TopicCode == "advanced"));
        Assert.All(lessons, l => { Assert.Equal(program.Id, l.ProgramId); Assert.Equal(2, l.RouteVersion); });
        Assert.Equal(4, teacher.Generated);
        var replies = await db.TelegramOutboxMessages.ToListAsync();
        Assert.Contains(replies, r => r.Text.Contains("Route v2", StringComparison.Ordinal));
        Assert.Contains(replies, r => r.Text.Contains("All route questions", StringComparison.Ordinal));
        Assert.All(replies.Where(r => r.ChatId == 202), r => Assert.DoesNotContain("algebra", r.Text));
        Assert.Equal(17, await db.ProcessedTelegramUpdates.CountAsync());
    }

    [Theory]
    [InlineData("learn_request")]
    [InlineData("learn_program")]
    [InlineData("learn_project")]
    public async Task Post_ProgramEntryModes_CreatesRouteAndAllowsSkippingDiagnosis(string command)
    {
        using var factory = new OwnDayHostFactory();
        var planner = new Planner();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(planner))));
        using var client = host.CreateClient();
        await PostAsync(client, 1, 101, "/project Build a website");
        var argument = command == "learn_project" ? "1" : "Build a website";
        await PostAsync(client, 2, 101, $"/{command} {argument} | Beginner");
        await PostAsync(client, 3, 101, "/learn_diagnose skip");
        await PostAsync(client, 3, 101, "/learn_diagnose skip");
        await PostAsync(client, 4, 101, "/learn_diagnose another answer");
        await PostAsync(client, 5, 101, "/learn_program other | Expert");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var programs = await db.Set<LearningProgram>().Include(p => p.Routes).ToListAsync();
        Assert.Equal(2, programs.Count);
        var program = Assert.Single(programs, p => p.Goal == "Build a website");
        Assert.Equal("Build a website", program.Goal);
        Assert.Equal(command == "learn_project" ? 1L : (long?)null, program.ProjectId);
        Assert.False(program.DiagnosticPending);
        Assert.Null(program.DiagnosticAnswer);
        Assert.Equal(1, program.CurrentRoute.Version);
        Assert.Equal(2, planner.Requests.Count);
        Assert.Equal(programs.Single(p => p.Goal == "other").Id, (await db.Set<LearningContext>().SingleAsync()).ProgramId);
        Assert.Equal(ProjectStatus.Active, (await db.Projects.SingleAsync()).Status);
        Assert.Empty(await db.Actions.ToListAsync());
    }

    [Fact]
    public async Task Post_ForeignOrMissingProject_RejectsBeforeCallingPlanner()
    {
        using var factory = new OwnDayHostFactory();
        var planner = new Planner();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(planner))));
        using var client = host.CreateClient();
        await PostAsync(client, 1, 101, "/project Private project");
        await PostAsync(client, 2, 202, "/learn_project 1 | Beginner");
        await PostAsync(client, 3, 101, "/learn_project 999 | Beginner");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.Set<LearningProgram>().ToListAsync());
        Assert.Empty(planner.Requests);
        Assert.All(await db.TelegramOutboxMessages.Where(m => m.ChatId == 202).ToListAsync(), m => Assert.DoesNotContain("Private project", m.Text));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cycle")]
    [InlineData("long question")]
    public async Task Post_InvalidInitialProposal_DoesNotPersistProgram(string kind)
    {
        using var factory = new OwnDayHostFactory();
        var planner = new Planner { Failure = kind == "failure", Invalid = kind == "cycle", LongQuestion = kind == "long question" };
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(planner))));
        using var client = host.CreateClient();
        await PostAsync(client, 1, 101, "/learn_program algebra | Beginner");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.Set<LearningProgram>().ToListAsync());
        Assert.Empty(await db.Set<CurriculumRoute>().ToListAsync());
        Assert.Contains("Retry", (await db.TelegramOutboxMessages.SingleAsync()).Text);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cycle")]
    [InlineData("second question")]
    [InlineData("different discipline")]
    public async Task Post_InvalidDiagnosticProposal_PreservesOriginalRouteAndPendingQuestion(string kind)
    {
        using var factory = new OwnDayHostFactory();
        var planner = new Planner();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(planner))));
        using var client = host.CreateClient();
        await PostAsync(client, 1, 101, "/learn_program algebra | Beginner");
        planner.Failure = kind == "failure";
        planner.Invalid = kind == "cycle";
        planner.SecondQuestion = kind == "second question";
        planner.DifferentDiscipline = kind == "different discipline";
        await PostAsync(client, 2, 101, "/learn_diagnose answer");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var program = await db.Set<LearningProgram>().Include(p => p.Routes).SingleAsync();
        Assert.True(program.DiagnosticPending);
        Assert.Equal("Mathematics", program.Discipline);
        Assert.Null(program.DiagnosticAnswer);
        Assert.Single(program.Routes);
        Assert.Equal(1, program.CurrentRoute.Version);
    }

    [Fact]
    public async Task Post_ClearExperience_StartsWithoutDiagnosticTest()
    {
        using var factory = new OwnDayHostFactory();
        var planner = new Planner { NoQuestion = true };
        var teacher = new Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(planner));
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
        }));
        using var client = host.CreateClient();
        await PostAsync(client, 1, 101, "/learn_program algebra | Experienced, start with equations");
        await PostAsync(client, 2, 101, "/learn_next");
        Assert.Equal(1, teacher.Generated);
        Assert.Single(planner.Requests);
        Assert.Null(planner.Requests[0].Answer);
        var context = Assert.IsType<LessonProgramContext>(Assert.Single(teacher.Requests).Program);
        Assert.Equal("Mathematics", context.Discipline);
        Assert.Equal("Experienced, start with equations", context.Experience);
        Assert.Null(context.DiagnosticQuestion);
        Assert.Null(context.DiagnosticAnswer);
    }

    internal static async Task PostAsync(HttpClient client, int id, long user, string text)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        request.Content = JsonContent.Create(new
        {
            update_id = id,
            message = new { message_id = id, date = 1700000000, chat = new { id = user, type = "private" },
                from = new { id = user, is_bot = false, first_name = "Learner" }, text }
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    internal sealed class Planner : ICurriculumPlanner
    {
        public CancellationTokenSource? CancelAfterPlan { get; set; }
        public CancellationToken ReceivedToken { get; private set; }
        public List<CurriculumRequest> Requests { get; } = [];
        public bool Failure { get; set; }
        public bool Invalid { get; set; }
        public bool LongQuestion { get; init; }
        public bool SecondQuestion { get; set; }
        public bool DifferentDiscipline { get; set; }
        public bool NoQuestion { get; init; }
        public Task<CurriculumProposal> PlanAsync(CurriculumRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            ReceivedToken = token;
            CancelAfterPlan?.Cancel();
            if (Failure) throw new LessonProviderException();
            return Task.FromResult(new CurriculumProposal(DifferentDiscipline ? "Physics" : "Mathematics",
                Invalid ? [new("a", "A", ["a"])] : [new("advanced", "Advanced", ["basics"]), new("basics", "Basics", [])],
                NoQuestion || request.Answer is not null && !SecondQuestion ? null : LongQuestion ? new string('q', 1001) : "Solve x + 2 = 4"));
        }
    }

    internal sealed class Teacher : ILessonTeacher
    {
        public CancellationTokenSource? CancelAfterGenerate { get; set; }
        public CancellationToken ReceivedToken { get; private set; }
        public int Generated { get; private set; }
        public List<LessonRequest> Requests { get; } = [];
        public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            Generated++;
            ReceivedToken = token;
            CancelAfterGenerate?.Cancel();
            return Task.FromResult(new LessonMaterial("Theory", "Question"));
        }
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token) =>
            Task.FromResult(new LessonAssessment("Feedback", answer == "correct"));
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
