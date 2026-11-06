using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramAdaptiveTeachingTests
{
    [Fact]
    public async Task Post_FocusedTopicWithTransitivePrerequisites_RetriesBeforeIndependentTopic()
    {
        using var factory = new OwnDayHostFactory();
        var planner = new Planner
        {
            Topics = [new("z", "Root", []), new("c", "Other", []), new("a", "Intermediate", ["z"]), new("b", "Selected", ["a"])]
        };
        var teacher = new Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, planner, teacher)));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_program algebra | Beginner");
        await Post(client, 2, "/learn_next");
        await Post(client, 3, "/answer correct");
        await Post(client, 4, "/learn_focus a");
        await Post(client, 5, "/answer correct");
        await Post(client, 6, "/learn_focus c");
        await Post(client, 7, "/learn_finish");
        await Post(client, 8, "/learn_focus b");
        await Post(client, 9, "/answer wrong");
        await Post(client, 10, "/learn_next");

        Assert.Equal("Selected", teacher.Requests[^1].Topic);
        Assert.Empty(planner.Requests);
        await Post(client, 11, "/answer correct");
        await Post(client, 12, "/learn_next");
        Assert.Equal("Other", teacher.Requests[^1].Topic);
    }

    [Fact]
    public async Task Post_RepeatedErrors_CorrectsThenAdvancesWithSameSkillAndAtomicReplies()
    {
        using var factory = new OwnDayHostFactory();
        var planner = new Planner();
        var teacher = new Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, planner, teacher)));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_program algebra | Beginner");
        await Post(client, 2, "/learn_next");
        await Post(client, 3, "/answer wrong");
        await Post(client, 4, "/learn_next");
        await Post(client, 5, "/answer wrong again");
        await Post(client, 6, "/learn_next");
        await Post(client, 6, "/learn_next");
        var request = Assert.Single(planner.Requests);
        Assert.True(request.State.RepeatedDifficulty);
        Assert.Equal(2, request.State.NegativeAssessments);
        Assert.Equal(2, request.RecentFeedback.Length);
        Assert.Equal(TeachingAction.Simplify, teacher.Requests[^1].Teaching!.Action);
        await Post(client, 7, "/answer correct");
        await Post(client, 8, "/learn_next");
        Assert.Equal("Advanced", teacher.Requests[^1].Topic);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var program = await db.Set<LearningProgram>().Include(p => p.Routes).SingleAsync();
        Assert.Equal(2, program.CurrentRoute.Version);
        Assert.Equal(1, program.CurrentRoute.ProgressVersion);
        Assert.Equal("algebra", program.Goal);
        Assert.Contains("2 negative", program.CurrentRoute.Reason);
        var attempts = await db.Set<Lesson>().Where(l => l.TopicCode == "basics").ToListAsync();
        Assert.Equal(3, attempts.Count);
        Assert.Single(attempts.Select(l => l.SkillId).Distinct());
        Assert.Equal(2, attempts.Single(l => l.IsCorrect == true).RouteVersion);
        Assert.Equal(3, await db.Set<LearningEvidence>().CountAsync());
        Assert.Equal(2, await db.Set<Skill>().CountAsync());
        Assert.Equal(8, await db.ProcessedTelegramUpdates.CountAsync());
        var messages = await db.TelegramOutboxMessages.ToListAsync();
        Assert.Single(messages, m => m.Text.StartsWith("Teaching adjustment", StringComparison.Ordinal));
        Assert.All(messages, m => Assert.InRange(m.Text.Length, 1, 4096));
    }

    [Theory]
    [InlineData("ReviewPrerequisite")]
    [InlineData("Advance")]
    [InlineData("Hint")]
    [InlineData("Explain")]
    [InlineData("Practice")]
    public async Task Post_ExplicitAdaptation_PreservesCompletedPrerequisiteAndSkillIdentity(string action)
    {
        using var factory = new OwnDayHostFactory();
        var planner = new Planner { Proposal = new(action, action == "ReviewPrerequisite" ? "basics" : "advanced", "Use the observed prerequisite result.") };
        var teacher = new Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, planner, teacher)));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_program algebra | Beginner");
        await Post(client, 2, "/learn_next");
        await Post(client, 3, "/answer correct");
        await Post(client, 4, "/learn_adapt");
        await Post(client, 4, "/learn_adapt");
        Assert.Equal(Enum.Parse<TeachingAction>(action), teacher.Requests[^1].Teaching!.Action);
        Assert.Equal(action == "ReviewPrerequisite" ? "Basics" : "Advanced", teacher.Requests[^1].Topic);
        await Post(client, 5, "/learn_finish");
        await Post(client, 6, "/learn_next");
        Assert.Equal("Advanced", teacher.Requests[^1].Topic);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var basics = await db.Set<Lesson>().Where(l => l.TopicCode == "basics").ToListAsync();
        Assert.Single(basics.Select(l => l.SkillId).Distinct());
        Assert.Equal(2, (await db.Set<LearningProgram>().Include(p => p.Routes).SingleAsync()).Routes.Count);
    }

    [Theory]
    [InlineData("unknown action")]
    [InlineData("unknown topic")]
    [InlineData("blocked topic")]
    [InlineData("empty reason")]
    [InlineData("long reason")]
    [InlineData("provider failure")]
    [InlineData("invalid material")]
    [InlineData("advance after errors")]
    public async Task Post_InvalidProposalOrMaterial_PreservesRouteLessonsKnowledgeAndHistory(string failure)
    {
        using var factory = new OwnDayHostFactory();
        var planner = new Planner();
        var teacher = new Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, planner, teacher)));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_program algebra | Beginner");
        await Post(client, 2, "/learn_next");
        await Post(client, 3, "/answer wrong");
        await Post(client, 4, "/learn_next");
        await Post(client, 5, "/answer wrong again");
        planner.Proposal = failure switch
        {
            "unknown action" => new("Delete", "basics", "Reason"),
            "unknown topic" => new("Practice", "foreign", "Reason"),
            "blocked topic" => new("Advance", "advanced", "Reason"),
            "empty reason" => new("Practice", "basics", " "),
            "long reason" => new("Practice", "basics", new string('x', 251)),
            "advance after errors" => new("Advance", "basics", "Skip the error"),
            _ => planner.Proposal
        };
        planner.Failure = failure == "provider failure";
        teacher.Invalid = failure == "invalid material";
        await Post(client, 6, "/learn_next");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var program = await db.Set<LearningProgram>().Include(p => p.Routes).SingleAsync();
        Assert.Single(program.Routes);
        Assert.Equal(2, await db.Set<Lesson>().CountAsync());
        Assert.Equal(2, await db.Set<LearningEvidence>().CountAsync());
        Assert.Single(await db.Set<Skill>().ToListAsync());
        Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), m => m.Text.Contains("temporarily unavailable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdaptAsync_CancelledAfterProvider_PreservesTrackedStateAndPropagatesToken(bool duringGeneration)
    {
        using var factory = new OwnDayHostFactory();
        var planner = new Planner();
        var teacher = new Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, planner, teacher)));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_program algebra | Beginner");
        using var cancellation = new CancellationTokenSource();
        if (duringGeneration) teacher.Cancellation = cancellation;
        else planner.Cancellation = cancellation;
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CurriculumService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AdaptAsync(new(101), cancellation.Token));
        Assert.Equal(cancellation.Token, duringGeneration ? teacher.Token : planner.Token);
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified);
        Assert.Single((await db.Set<LearningProgram>().Include(p => p.Routes).SingleAsync()).Routes);
    }

    [Fact]
    public async Task Post_ManualDirection_RespectsPrerequisitesOwnershipAndActiveSession()
    {
        using var factory = new OwnDayHostFactory();
        var planner = new Planner();
        var teacher = new Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => Configure(services, planner, teacher)));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_program algebra | Beginner");
        await Post(client, 2, "/learn_focus advanced");
        await Post(client, 3, "/learn_focus missing");
        await Post(client, 4, "/learn_focus other");
        await Post(client, 4, "/learn_focus other");
        await Post(client, 5, "/learn_adapt");
        await Post(client, 6, "/learn_pause");
        await Post(client, 7, "/learn_focus basics");
        await TelegramCurriculumTests.PostAsync(client, 8, 202, "/learn_focus other");
        await TelegramCurriculumTests.PostAsync(client, 9, 202, "/learn_adapt");
        Assert.Empty(planner.Requests);
        Assert.Equal("Other", Assert.Single(teacher.Requests).Topic);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var program = await db.Set<LearningProgram>().Include(p => p.Routes).SingleAsync();
        Assert.Equal("other", program.CurrentRoute.Topics[0].Code);
        Assert.Equal("algebra", program.Goal);
        Assert.Contains("User selected", program.CurrentRoute.Reason);
        Assert.Equal(LessonStatus.Paused, (await db.Set<Lesson>().SingleAsync()).Status);
        Assert.All(await db.TelegramOutboxMessages.Where(m => m.ChatId == 202).ToListAsync(), m => Assert.DoesNotContain("algebra", m.Text));
    }

    private static Task Post(HttpClient client, int id, string text) => TelegramCurriculumTests.PostAsync(client, id, 101, text);

    internal static void Configure(IServiceCollection services, Planner planner, Teacher teacher)
    {
        services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(planner));
        services.Replace(ServiceDescriptor.Singleton<ITeachingPlanner>(planner));
        services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
    }

    internal sealed class Planner : ICurriculumPlanner, ITeachingPlanner
    {
        public CurriculumTopic[] Topics { get; init; } = [new("basics", "Basics", []), new("advanced", "Advanced", ["basics"]), new("other", "Other", [])];
        public TeachingProposal Proposal { get; set; } = new("Simplify", "basics", "Repeated negative AI assessments suggest a smaller concrete example.");
        public List<TeachingRequest> Requests { get; } = [];
        public bool Failure { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public CancellationToken Token { get; private set; }
        public Task<CurriculumProposal> PlanAsync(CurriculumRequest request, CancellationToken token) => Task.FromResult(
            new CurriculumProposal("Mathematics", Topics, null));
        public Task<TeachingProposal> ProposeAsync(TeachingRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            Token = token;
            Cancellation?.Cancel();
            if (Failure) throw new LessonProviderException();
            return Task.FromResult(Proposal);
        }
    }

    internal sealed class Teacher : ILessonTeacher
    {
        public List<LessonRequest> Requests { get; } = [];
        public CancellationTokenSource? Cancellation { get; set; }
        public CancellationToken Token { get; private set; }
        public bool Invalid { get; set; }
        public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            Token = token;
            Cancellation?.Cancel();
            return Task.FromResult(new LessonMaterial(Invalid ? "" : "Theory", "Question"));
        }
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token) =>
            Task.FromResult(new LessonAssessment("Try a smaller example.", answer == "correct"));
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) => throw new NotSupportedException();
    }
}
