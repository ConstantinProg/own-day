using System.Data;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Application.Dialogue;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.Learning;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.OpenAIAnalysis;
using OwnDay.Infrastructure.Telegram.Handling;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot.Types;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

[Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
public sealed partial class PostgresLearningProviderTests : IAsyncLifetime
{
    private readonly string _schema = "ownday_test_" + Guid.NewGuid().ToString("N");
    private readonly UserId _user = new(101);
    private string _connection = "";
    private static readonly DateTimeOffset _now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        _connection = (Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required.")) + ";Search Path=" + _schema;
        await using var db = Open();
#pragma warning disable EF1003 // Isolated GUID schema.
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + _schema);
#pragma warning restore EF1003
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var db = Open();
#pragma warning disable EF1003 // Isolated GUID schema.
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA " + _schema + " CASCADE");
#pragma warning restore EF1003
    }

    [Theory]
    [InlineData("learn", "generate")]
    [InlineData("answer", "assess")]
    [InlineData("learn_ask", "explain")]
    [InlineData("learn_explain", "explain")]
    [InlineData("learn_practice", "generate")]
    [InlineData("learn_review", "generate")]
    [InlineData("learn_request", "plan")]
    [InlineData("learn_program", "plan")]
    [InlineData("learn_project", "plan")]
    [InlineData("learn_diagnose", "plan")]
    [InlineData("learn_next", "generate")]
    [InlineData("learn_adapt", "teach")]
    [InlineData("learn_adapt", "generate")]
    [InlineData("learn_focus", "generate")]
    [InlineData("learn_project_practice", "generate")]
    [InlineData("learn_transfer", "generate")]
    [InlineData("learn_invite_start", "generate")]
    public async Task Process_EachProviderPath_ReleasesTargetsTransactionAndConnectionBeforeExternalWork(string command, string blockedKind)
    {
        var arguments = await SeedAsync(command);
        var probe = new Probe(blockedKind);
        using var host = Host(probe, worker: true);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        var response = await client.PostAsJsonAsync("/telegram/webhook", new
        {
            update_id = 1,
            message = new { message_id = 1, date = 0, chat = new { id = 101, type = "private" },
                from = new { id = 101, is_bot = false, first_name = "Test" }, text = "/" + command + " " + arguments }
        });
        response.EnsureSuccessStatusCode();
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await using var observer = Open();
            observer.Database.SetCommandTimeout(3);
            await using (var transaction = await observer.Database.BeginTransactionAsync())
            {
                await observer.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({_user.Value})");
                Assert.Equal(LearningRequestStatus.Running, (await observer.Set<LearningRequest>().AsNoTracking().SingleAsync()).Status);
                var checkpoint = await observer.Set<LearningProviderStep>().AsNoTracking().SingleAsync(step => step.Kind == blockedKind);
                Assert.NotEmpty(checkpoint.Input);
                Assert.Equal(64, checkpoint.TargetStamp.Length);
                Assert.Null(checkpoint.Result);
            }
            Assert.False(probe.Release.Task.IsCompleted);
        }
        finally { probe.Release.TrySetResult(); }
        await WaitCompletedAsync();
        await using var result = Open();
        Assert.Equal("reply_committed", (await result.Set<LearningRequest>().SingleAsync()).Outcome);
        Assert.Single(await result.Set<LearningAppliedOperation>().ToListAsync());
        Assert.NotEmpty(await result.TelegramOutboxMessages.ToListAsync());
        Assert.All(await result.Set<LearningProviderStep>().ToListAsync(), step => Assert.NotNull(step.Result));
        Assert.Equal(command == "learn_adapt" ? 2 : 1, probe.Calls);
        if (command == "answer") Assert.Single(await result.Set<LearningEvidence>().Where(e => e.ObservedAt == _now).ToListAsync());
        else if (command is "learn_ask" or "learn_explain")
        {
            var lesson = await result.Set<Lesson>().Include(l => l.Interactions).SingleAsync();
            Assert.Single(lesson.Interactions);
            Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
            Assert.Equal("What is requested?", lesson.Question);
        }
        else if (command is "learn_request" or "learn_program" or "learn_project")
        {
            Assert.Equal(2, await result.Set<LearningProgram>().CountAsync());
            var selected = (await result.Set<LearningContext>().SingleAsync()).ProgramId;
            Assert.Equal("Work emails", (await result.Set<LearningProgram>().SingleAsync(p => p.Id == selected)).Goal);
        }
        else if (command == "learn_diagnose")
        {
            var program = await result.Set<LearningProgram>().Include(p => p.Routes).SingleAsync();
            Assert.False(program.DiagnosticPending);
            Assert.Equal(2, program.Routes.Count);
        }
        else
        {
            var lesson = await result.Set<Lesson>().SingleAsync(l => l.Status == LessonStatus.AwaitingAnswer);
            Assert.Equal("Theory", lesson.Theory);
            Assert.Equal("Question", lesson.Question);
            Assert.Equal(command == "learn_review", lesson.IsReview);
            if (command == "learn_project_practice") Assert.NotNull(lesson.ProjectActionId);
            if (command == "learn_transfer") Assert.NotNull(lesson.Transfer);
            if (command == "learn_invite_start") Assert.Null((await result.Set<StudySchedule>().SingleAsync()).InvitationId);
        }
    }

    [Theory]
    [InlineData("answer")]
    [InlineData("learn_ask")]
    [InlineData("learn_next")]
    [InlineData("answer-restored")]
    public async Task Process_TargetChangedDuringProvider_RejectsResultWithoutEvidenceOrRouteMutation(string command)
    {
        var restore = command == "answer-restored";
        if (restore) command = "answer";
        var arguments = await SeedAsync(command);
        var probe = new Probe(command == "answer" ? "assess" : command == "learn_ask" ? "explain" : "generate");
        using var host = Host(probe, worker: true);
        using var client = host.CreateClient();
        await EnqueueAsync(command, arguments);
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await using var db = Open();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({_user.Value})");
            if (command == "learn_next")
                db.Add(LearningPreference.Create(_user, LearningPreferenceKind.Format, "Short", _now));
            else
            {
                var lesson = await db.Set<Lesson>().SingleAsync(l => l.Status == LessonStatus.AwaitingAnswer);
                lesson.Pause();
                await db.SaveChangesAsync();
                if (restore) lesson.Resume();
            }
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        finally { probe.Release.TrySetResult(); }
        await WaitCompletedAsync();
        await using var observer = Open();
        Assert.Equal("stale_target", (await observer.Set<LearningRequest>().SingleAsync()).Outcome);
        var accounted = await observer.OpenAIRequestAttempts.SingleAsync();
        Assert.NotNull(accounted.LearningProviderAttemptId);
        Assert.NotNull(accounted.ChargedUsd);
        Assert.Equal(accounted.ReservedUsd, accounted.ChargedUsd);
        Assert.Empty(await observer.Set<LearningEvidence>().Where(e => e.ObservedAt == _now).ToListAsync());
        Assert.Empty(await observer.Set<Lesson>().Where(l => l.Status == LessonStatus.Completed && l.Answer == "A reply").ToListAsync());
    }

    private async Task<string> SeedAsync(string command)
    {
        await using var db = Open();
        var project = Project.Create(_user, "Work emails", _now.UtcDateTime);
        db.Add(project);
        await db.SaveChangesAsync();
        var program = LearningProgram.Create(_user, "Work emails", "Beginner", project.Id, "English",
            [new("root", "Read an email", [])], command == "learn_diagnose" ? "What do you know?" : null);
        var skill = Skill.Create(_user, "Read an email", "Identify a request");
        db.AddRange(program, skill, LearningContext.Create(program));
        if (command != "learn_diagnose") db.Add(ProgramSkillLink.Confirm(program, "root", skill, "Confirmed identity", _now));
        string arguments = command switch
        {
            "learn" => "English", "answer" => "A reply", "learn_ask" => "Why?",
            "learn_request" or "learn_program" => "Work emails | Beginner",
            "learn_project" => project.Id + " | Beginner",
            "learn_diagnose" => "A little", "learn_focus" => "root",
            "learn_practice" or "learn_review" => skill.Id.ToString(), _ => ""
        };
        if (command is "answer" or "learn_ask" or "learn_explain")
        {
            var lesson = Lesson.Start(_user, "English", "Read this email", "What is requested?");
            lesson.LinkToSkill(skill);
            db.Add(lesson);
        }
        if (command == "learn_review")
        {
            var old = Lesson.Start(_user, "English", "Old", "Old?");
            old.LinkToSkill(skill);
            old.Complete("Old answer", "Old feedback", true);
            var evidence = LearningEvidence.FromAnswer(old, DateTimeOffset.UnixEpoch, "test");
            db.AddRange(old, evidence, ReviewSchedule.FromAnswer(evidence));
        }
        if (command == "learn_project_practice")
        {
            var action = OwnDay.Domain.Actions.Action.Create(_user, "Reply", _now.UtcDateTime, project.Id);
            db.Add(action);
            await db.SaveChangesAsync();
            arguments = "root | " + action.Id;
        }
        if (command == "learn_transfer")
        {
            var source = LearningProgram.Create(_user, "French emails", "Beginner", null, "French", [new("source", "Read French", [])], null);
            db.Add(source);
            db.Add(ProgramSkillLink.Confirm(source, "source", skill, "Same skill", _now));
            arguments = "root | " + source.Id + " | source";
        }
        if (command == "learn_invite_start")
        {
            var schedule = StudySchedule.Create(_user, "UTC", 1, [new(new(0, 0), new(23, 59))], _now);
            arguments = schedule.Invite(program.Id, null, _now).ToString();
            db.Add(schedule);
        }
        await db.SaveChangesAsync();
        return arguments;
    }

    [Fact]
    public async Task Generate_SourceReadFails_UsesUnavailableProvenanceOutsideDatabaseScope()
    {
        var probe = new Probe("source");
        using var host = Host(probe, worker: true, source: true);
        using var client = host.CreateClient();
        await EnqueueAsync("learn", "JSON");
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await using var observer = Open();
            observer.Database.SetCommandTimeout(3);
            await using var transaction = await observer.Database.BeginTransactionAsync();
            await observer.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({_user.Value})");
            Assert.Empty(await observer.Set<Lesson>().ToListAsync());
            Assert.Single(await observer.Set<LearningProviderStep>().ToListAsync());
        }
        finally { probe.Release.TrySetResult(); }
        await WaitCompletedAsync();
        await using var result = Open();
        Assert.Equal(MaterialSourceStatus.Unavailable, (await result.Set<Lesson>().SingleAsync()).Provenance!.Status);
        var accounted = await result.OpenAIRequestAttempts.SingleAsync();
        Assert.Equal(30, accounted.InputTokens);
        Assert.Equal(10, accounted.OutputTokens);
        Assert.Equal(5, accounted.CachedInputTokens);
        Assert.Equal(0.00000755m, accounted.EstimatedUsd);
        Assert.Equal(accounted.EstimatedUsd, accounted.ChargedUsd);
        Assert.True(accounted.FinishedAt >= accounted.StartedAt);
    }

    [Theory]
    [InlineData("learn_adapt")]
    [InlineData("learn_invite_start")]
    public async Task Process_GenerationFails_PreservesRouteSelectionAndUnresolvedInvitation(string command)
    {
        var arguments = await SeedAsync(command);
        Guid selected;
        await using (var db = Open())
        {
            var current = await db.Set<LearningContext>().SingleAsync();
            if (command == "learn_invite_start")
            {
                var other = LearningProgram.Create(_user, "Other goal", "Beginner", null, "Other", [new("other", "Other topic", [])], null);
                db.Add(other);
                current.Select(other);
                await db.SaveChangesAsync();
            }
            selected = current.ProgramId;
        }
        var probe = new Probe("generate") { FailureKind = "generate" };
        using var host = Host(probe, worker: true);
        using var client = host.CreateClient();
        await EnqueueAsync(command, arguments);
        try { await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { probe.Release.TrySetResult(); }
        await WaitCompletedAsync();
        await using var observer = Open();
        Assert.Equal(selected, (await observer.Set<LearningContext>().SingleAsync()).ProgramId);
        Assert.Empty(await observer.Set<Lesson>().ToListAsync());
        Assert.All(await observer.Set<LearningProgram>().Include(p => p.Routes).ToListAsync(), program => Assert.Single(program.Routes));
        if (command == "learn_invite_start") Assert.NotNull((await observer.Set<StudySchedule>().SingleAsync()).InvitationId);
        if (command == "learn_adapt")
        {
            var steps = await observer.Set<LearningProviderStep>().OrderBy(step => step.Ordinal).ToArrayAsync();
            Assert.Equal(2, steps.Length);
            Assert.NotNull(steps[0].Result);
            Assert.Equal(LessonProviderFailure.HttpError, steps[1].Failure);
        }
    }

    private async Task EnqueueAsync(string command, string arguments)
    {
        await using var db = Open();
        db.Add(Request(command, arguments));
        await db.SaveChangesAsync();
    }

    private static LearningRequest Request(string name, string arguments) => new()
    {
        UserId = new(101), Sequence = 1, SourceKey = "telegram:update:1", SourceUpdateId = 1,
        SourceMessageId = 1, ChatId = 101, Status = LearningRequestStatus.Ready, Outcome = null,
        ReceivedAt = DateTime.UtcNow, AvailableAt = DateTime.UtcNow,
        NormalizedInput = JsonSerializer.Serialize(new LearningCommandInput(name, arguments), JsonSerializerOptions.Web)
    };

    private async Task WaitCompletedAsync()
    {
        for (var i = 0; i < 200; i++)
        {
            await using var db = Open();
            if (await db.Set<LearningRequest>().AnyAsync(r => r.Status == LearningRequestStatus.Completed)) return;
            await Task.Delay(50);
        }
        Assert.Fail("Worker did not complete the request.");
    }

    private OwnDayDbContext Open() => new(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(_connection).Options);

    private WebApplicationFactory<Program> Host(Probe probe, bool worker, bool source = false,
        Action<LearningRequestProcessingOptions>? configure = null, IInterceptor? interceptor = null,
        bool publicIngress = false) => new OwnDayHostFactory().WithWebHostBuilder(builder =>
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OwnDayDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<OwnDayDbContext>>();
            services.AddDbContext<OwnDayDbContext>(options =>
            {
                options.UseNpgsql(_connection);
                if (interceptor is not null) options.AddInterceptors(interceptor);
            });
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new Clock()));
            services.Replace(ServiceDescriptor.Scoped<ILessonTeacher>(provider => new Provider(probe, provider.GetRequiredService<OwnDayDbContext>(), provider.GetRequiredService<LearningProviderUsage>(), source)));
            services.Replace(ServiceDescriptor.Scoped<ICurriculumPlanner>(provider => new Provider(probe, provider.GetRequiredService<OwnDayDbContext>(), provider.GetRequiredService<LearningProviderUsage>())));
            services.Replace(ServiceDescriptor.Scoped<ITeachingPlanner>(provider => new Provider(probe, provider.GetRequiredService<OwnDayDbContext>(), provider.GetRequiredService<LearningProviderUsage>())));
            services.Replace(ServiceDescriptor.Scoped<ILearningIntentResolver>(provider => new Provider(probe, provider.GetRequiredService<OwnDayDbContext>(), provider.GetRequiredService<LearningProviderUsage>())));
            if (!publicIngress) services.Replace(ServiceDescriptor.Scoped<ITelegramUpdateHandler, TestIngress>());
            // Keep startup configuration valid; worker controls hosted execution so tests can process manually.
            services.Configure<LearningRequestProcessingOptions>(options => { options.Enabled = true; options.PollMilliseconds = 20; });
            if (configure is not null) services.Configure(configure);
            if (worker) services.AddHostedService<LearningRequestWorker>();
        });
    });

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => _now; }
    private sealed class Probe(string blockedKind)
    {
        public string BlockedKind { get; } = blockedKind;
        public string? FailureKind { get; init; }
        public int FailureCount { get; init; } = int.MaxValue;
        public LessonProviderFailure FailureReason { get; init; } = LessonProviderFailure.HttpError;
        public System.Collections.Concurrent.ConcurrentDictionary<string, int> KindCalls { get; } = new();
        public bool IgnoreCancellation { get; init; }
        public LearningIntent Intent { get; init; } = LearningIntent.Answer;
        public CurriculumProposal Curriculum { get; init; } = new("English", [new("root", "Read an email", [])], null);
        public Func<CurriculumRequest, CurriculumProposal>? Plan { get; init; }
        public LessonMaterial Material { get; init; } = new("Theory", "Question");
        public Func<LessonSnapshot, string, LessonAssessment>? Assessment { get; init; }
        public System.Collections.Concurrent.ConcurrentQueue<LearningIntentInput> IntentInputs { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<string> AssessedAnswers { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<LessonSnapshot> AssessedLessons { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<LessonInteractionKind> ExplanationKinds { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
    }

    private sealed class Provider(Probe probe, OwnDayDbContext db, LearningProviderUsage usage, bool source = false) : ILessonTeacher, ICurriculumPlanner, ITeachingPlanner, ILearningIntentResolver
    {
        private async Task BlockAsync(string kind, CancellationToken token)
        {
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
            Assert.NotNull(usage.LedgerAttemptId);
            var admission = await db.OpenAIRequestAttempts.AsNoTracking().SingleAsync(a => a.Id == usage.LedgerAttemptId, token);
            Assert.NotNull(admission.LearningProviderAttemptId);
            Assert.Null(admission.AnalysisId);
            Interlocked.Increment(ref probe.Calls);
            var kindCall = probe.KindCalls.AddOrUpdate(kind, 1, (_, count) => count + 1);
            if (kind == probe.BlockedKind)
            {
                probe.Started.TrySetResult();
                if (probe.IgnoreCancellation) await probe.Release.Task;
                else await probe.Release.Task.WaitAsync(token);
            }
            if (kind == probe.FailureKind && kindCall <= probe.FailureCount) throw new LessonProviderException(probe.FailureReason);
        }
        public async Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token)
        {
            if (!source) { await BlockAsync("generate", token); return probe.Material; }
            using var sourceHttp = new HttpClient(new Handler(async cancellationToken =>
            {
                await BlockAsync("source", cancellationToken);
                return new(HttpStatusCode.ServiceUnavailable);
            }));
            using var reader = new RfcLessonSourceReader(sourceHttp, TimeProvider.System);
            using var apiHttp = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id = "test", model = "gpt-6-luna", created = 1, @object = "chat.completion",
                    usage = new { prompt_tokens = 30, completion_tokens = 10, total_tokens = 40,
                        prompt_tokens_details = new { cached_tokens = 5 } },
                    choices = new[] { new { index = 0, finish_reason = "stop", message = new
                    { role = "assistant", content = """{"Theory":"Theory","Question":"Question","ExpectedAnswer":null,"SourceQuote":null,"Criteria":[{"Code":"explain","Requirement":"Explain your answer."}]}""" } } }
                }), Encoding.UTF8, "application/json")
            })));
            var teacher = new OpenAILessonTeacher(() => new ChatClient("test", new ApiKeyCredential("dummy-key"), new OpenAIClientOptions
            {
                Transport = new HttpClientPipelineTransport(apiHttp), RetryPolicy = new ClientRetryPolicy(0)
            }), Options.Create(new OpenAIInboxAnalysisOptions { Enabled = true, ApiKey = "dummy-key" }), NullLogger<OpenAILessonTeacher>.Instance, reader, usage);
            return await teacher.GenerateAsync(request, token);
        }
        public async Task<LessonAssessment> AssessAsync(LessonSnapshot lesson, string answer, CancellationToken token)
        { probe.AssessedAnswers.Enqueue(answer); probe.AssessedLessons.Enqueue(lesson); await BlockAsync("assess", token); return probe.Assessment?.Invoke(lesson, answer) ?? new("Feedback", true); }
        public async Task<LearningIntentProposal> ResolveAsync(LearningIntentInput input, CancellationToken token)
        { probe.IntentInputs.Enqueue(input); await BlockAsync("intent", token); return new(probe.Intent); }
        public async Task<LessonExplanation> ExplainAsync(LessonSnapshot lesson, string request, LessonInteractionKind kind, CancellationToken token)
        { probe.ExplanationKinds.Enqueue(kind); await BlockAsync("explain", token); return new("Explanation"); }
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token) => throw new InvalidOperationException("Tracked lesson crossed provider boundary.");
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) => throw new InvalidOperationException("Tracked lesson crossed provider boundary.");
        public async Task<CurriculumProposal> PlanAsync(CurriculumRequest request, CancellationToken token)
        { await BlockAsync("plan", token); return probe.Plan?.Invoke(request) ?? probe.Curriculum; }
        public async Task<TeachingProposal> ProposeAsync(TeachingRequest request, CancellationToken token)
        { await BlockAsync("teach", token); return new("Simplify", "root", "Use a shorter email"); }
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(cancellationToken);
    }

    private sealed class TestIngress(OwnDayDbContext db, TelegramUpdateRouter router) : ITelegramUpdateHandler
    {
        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var dispatch = Assert.IsType<TelegramUpdateRouteResult.Dispatch>(router.Route(update));
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO processed_telegram_updates (update_id, received_at) VALUES ({update.Id}, {DateTime.UtcNow}) ON CONFLICT DO NOTHING
                """, cancellationToken);
            if (inserted == 0) return;
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({dispatch.Command.UserId.Value})", cancellationToken);
            db.Add(Request(dispatch.Command.Name, dispatch.Command.Arguments));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }
}
