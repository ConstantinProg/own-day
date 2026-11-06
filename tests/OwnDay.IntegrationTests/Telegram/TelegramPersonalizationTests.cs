using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramPersonalizationTests
{
    [Fact]
    public async Task Post_PreferencesChangedAndReset_PersonalizesNewLessonsAndPreservesSnapshots()
    {
        using var factory = new OwnDayHostFactory();
        var teacher = new TelegramCurriculumTests.Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher))));
        using var client = host.CreateClient();
        await Post(client, 1, "/learn_preference format | dialogue");
        await Post(client, 1, "/learn_preference format | dialogue");
        using (var read = host.Services.CreateScope())
        {
            var initialReplies = await read.ServiceProvider.GetRequiredService<OwnDayDbContext>().TelegramOutboxMessages.ToListAsync();
            Assert.Contains(initialReplies, r => r.Text.Contains("Format: dialogue", StringComparison.Ordinal));
            Assert.DoesNotContain(initialReplies, r => r.Text.Contains("Format: unspecified", StringComparison.Ordinal));
        }
        await Post(client, 2, "/learn_preference depth | detailed");
        await Post(client, 3, "/learn_preference pace | one step at a time");
        await Post(client, 4, "/learn_preference examples | music");
        await Post(client, 5, "/learn Algebra");
        await Post(client, 6, "/learn_preference depth | brief");
        await Post(client, 7, "/learn_finish");
        await Post(client, 8, "/learn Algebra");
        await Post(client, 9, "/answer correct");
        await Post(client, 9, "/answer correct");
        await Post(client, 10, "/learn_preferences");
        await TelegramCurriculumTests.PostAsync(client, 11, 202, "/learn_preferences");
        await Post(client, 12, "/learn_preference_reset depth");
        await Post(client, 13, "/learn_preference_reset all");
        using (var read = host.Services.CreateScope())
        {
            var resetReplies = await read.ServiceProvider.GetRequiredService<OwnDayDbContext>().TelegramOutboxMessages.ToListAsync();
            Assert.Contains(resetReplies, r => r.ChatId == 101 && r.Text.Contains("Format: unspecified", StringComparison.Ordinal));
        }
        await Post(client, 14, "/learn Algebra");
        Assert.Equal("detailed", teacher.Requests[0].Personalization!.ExplicitPreferences.Depth);
        Assert.Equal("brief", teacher.Requests[1].Personalization!.ExplicitPreferences.Depth);
        Assert.Equal("music", teacher.Requests[0].Personalization!.ExplicitPreferences.Examples);
        Assert.Null(teacher.Requests[2].Personalization!.ExplicitPreferences.Format);
        Assert.Empty(teacher.Requests[2].Personalization!.StrategyEvidence);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var lessons = await db.Set<Lesson>().ToListAsync();
        Assert.Contains(lessons, l => l.Teaching!.Preferences.Depth == "detailed");
        Assert.Empty(await db.Set<LearningPreference>().ToListAsync());
        var profile = await scope.ServiceProvider.GetRequiredService<KnowledgeService>().PersonalizationAsync(new(101), default);
        Assert.Equal(1, Assert.Single(profile.StrategyEvidence).Answers);
        Assert.Single(await db.Set<LearningEvidence>().ToListAsync());
        var replies = await db.TelegramOutboxMessages.ToListAsync();
        Assert.Contains(replies, r => r.Text.Contains("Insufficient evidence", StringComparison.Ordinal));
        Assert.All(replies.Where(r => r.ChatId == 202), r => Assert.DoesNotContain("music", r.Text));
        Assert.All(replies, r => Assert.InRange(r.Text.Length, 1, 4096));
    }

    [Theory]
    [InlineData("/learn_preference 0 | value")]
    [InlineData("/learn_preference format")]
    [InlineData("/learn_preference format | ")]
    [InlineData("/learn_preference unknown | value")]
    [InlineData("/learn_preference_reset 0")]
    [InlineData("/learn_preference_reset")]
    [InlineData("/learn_preferences extra")]
    public async Task Post_InvalidPreferenceCommand_ShowsUsageWithoutMutation(string command)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await Post(client, 1, command);
        await Post(client, 2, "/learn_preference examples | " + new string('x', 201));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.Set<LearningPreference>().ToListAsync());
        Assert.All(await db.TelegramOutboxMessages.ToListAsync(), r => Assert.Contains("/learn_preference <format", r.Text));
    }

    [Fact]
    public async Task PersonalizationAsync_RecentAssessedLessons_UsesBoundedOwnedEvidenceWithoutReclassifyingLegacyLessons()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var skill = Skill.Create(new(101), "Algebra", "Algebra");
        var foreign = Skill.Create(new(202), "Private", "Private");
        db.AddRange(skill, foreign);
        var time = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 104; i++)
        {
            var selected = i == 103 ? foreign : skill;
            var lesson = Lesson.Start(selected.UserId, selected.Title, "Theory", "Question");
            lesson.LinkToSkill(selected);
            if (i != 102) lesson.SetTeaching(new(i == 0 || i == 103 ? PedagogicalStrategy.Hint : PedagogicalStrategy.Practice,
                new(null, null, null, null)));
            lesson.Complete("Answer", "Provisional feedback", true);
            db.AddRange(lesson, LearningEvidence.FromAnswer(lesson, time.AddMinutes(i), "Fake AI"));
        }
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<KnowledgeService>();
        var summary = Assert.Single((await service.PersonalizationAsync(new(101), default)).StrategyEvidence);
        Assert.Equal(PedagogicalStrategy.Practice, summary.Strategy);
        Assert.Equal(100, summary.Answers);
        Assert.Equal(PedagogicalStrategy.Hint,
            Assert.Single((await service.PersonalizationAsync(new(202), default)).StrategyEvidence).Strategy);
    }

    [Fact]
    public async Task Post_ConfirmedTransfer_AssessesSharedSkillAndPreservesIndependentProgress()
    {
        using var factory = new OwnDayHostFactory();
        var teacher = new TelegramCurriculumTests.Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher))));
        using var client = host.CreateClient();
        var (source, target, skill) = await SeedTransferAsync(host.Services, "sourceUncompleted");
        await Post(client, 1, $"/learn_link basics | {skill} | Algebra applies to mechanics");
        await Post(client, 2, $"/learn_transfer basics | {source} | basics");
        await Post(client, 2, $"/learn_transfer basics | {source} | basics");
        await Post(client, 3, "/learn_pause");
        await Post(client, 4, "/learn_resume");
        await Post(client, 5, "/answer correct");
        await Post(client, 5, "/answer correct");
        var request = Assert.Single(teacher.Requests);
        Assert.Equal("Mathematics", request.Transfer!.Relation.SourceDiscipline);
        Assert.Equal("Physics", request.Transfer.Relation.TargetDiscipline);
        Assert.Equal(skill, request.Transfer.Relation.SkillId);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var transfer = await db.Set<Lesson>().SingleAsync(l => l.ProgramId == target);
        Assert.Equal(skill, transfer.SkillId);
        Assert.Equal(source, transfer.Transfer!.SourceProgramId);
        Assert.Equal(PedagogicalStrategy.InterdisciplinaryPractice, transfer.Teaching!.Strategy);
        Assert.Equal(2, await db.Set<LearningEvidence>().CountAsync());
        Assert.Single(await db.Set<Lesson>().Where(l => l.ProgramId == source).ToListAsync());
        Assert.Equal(1, (await db.Set<LearningProgram>().Include(p => p.Routes).SingleAsync(p => p.Id == source)).CurrentRoute.Version);
        var curriculum = scope.ServiceProvider.GetRequiredService<ICurriculumStore>();
        Assert.Empty(await curriculum.CompletedTopicsAsync(new(101), source, 1, default));
        Assert.Equal("basics", Assert.Single(await curriculum.CompletedTopicsAsync(new(101), target, 1, default)));
        Assert.All(await db.TelegramOutboxMessages.ToListAsync(), r => Assert.InRange(r.Text.Length, 1, 4096));
        Assert.Equal(3, await db.TelegramOutboxMessages.CountAsync(r => r.Text.Contains("Basis: Algebra applies to mechanics")));
    }

    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("differentSkill")]
    [InlineData("foreign")]
    [InlineData("missing")]
    [InlineData("sameProgram")]
    [InlineData("sameDiscipline")]
    [InlineData("diagnosis")]
    [InlineData("sourceDiagnosis")]
    [InlineData("prerequisite")]
    [InlineData("sourcePrerequisite")]
    [InlineData("unknownTopic")]
    [InlineData("activeLesson")]
    public async Task Post_UnsupportedTransfer_RejectsBeforeProviderCall(string scenario)
    {
        using var factory = new OwnDayHostFactory();
        var teacher = new TelegramCurriculumTests.Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher))));
        using var client = host.CreateClient();
        var (source, target, skill) = await SeedTransferAsync(host.Services, scenario);
        if (scenario != "unconfirmed") await Post(client, 1, $"/learn_link basics | {skill} | Confirmed");
        if (scenario == "missing") source = Guid.NewGuid();
        if (scenario == "sameProgram") source = target;
        await Post(client, 2, $"/learn_transfer {(scenario == "prerequisite" ? "advanced" : "basics")} | {source} | {(scenario == "unknownTopic" ? "missing" : scenario == "sourcePrerequisite" ? "advanced" : "basics")}");
        Assert.Empty(teacher.Requests);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.Set<Lesson>().Where(l => l.Transfer != null).ToListAsync());
        Assert.Single(await db.Set<LearningEvidence>().ToListAsync());
    }

    [Fact]
    public async Task TransferPracticeAsync_CanceledAfterGeneration_DoesNotMutateState()
    {
        using var factory = new OwnDayHostFactory();
        using var cancellation = new CancellationTokenSource();
        var teacher = new TelegramCurriculumTests.Teacher { CancelAfterGenerate = cancellation };
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher))));
        using var client = host.CreateClient();
        var (source, _, skill) = await SeedTransferAsync(host.Services);
        await Post(client, 1, $"/learn_link basics | {skill} | Confirmed");
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CurriculumService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TransferPracticeAsync(new(101), "basics", source, "basics", cancellation.Token));
        Assert.Equal(cancellation.Token, teacher.ReceivedToken);
        Assert.DoesNotContain(scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().ChangeTracker.Entries(),
            e => e.State is EntityState.Added or EntityState.Modified);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TransferPracticeAsync(new(101), "basics", source, "basics", cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => service.TransferPracticeAsync(default, "basics", source, "basics", default));
        Assert.Single(teacher.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_TransferProviderFailureOrInvalidMaterial_PreservesPreferencesAndLearningState(bool invalid)
    {
        using var factory = new OwnDayHostFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(new FailingTeacher(invalid)))));
        using var client = host.CreateClient();
        var (source, _, skill) = await SeedTransferAsync(host.Services);
        await Post(client, 1, $"/learn_link basics | {skill} | Confirmed");
        await Post(client, 2, "/learn_preference format | dialogue");
        await Post(client, 3, $"/learn_transfer basics | {source} | basics");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Single(await db.Set<Lesson>().ToListAsync());
        Assert.Single(await db.Set<LearningEvidence>().ToListAsync());
        Assert.Equal("dialogue", (await db.Set<LearningPreference>().SingleAsync()).Value);
        Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), m => m.Text.Contains("temporarily unavailable", StringComparison.Ordinal));
    }

    private sealed class FailingTeacher(bool invalid) : ILessonTeacher
    {
        public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token) => invalid
            ? Task.FromResult(new LessonMaterial("", "Question")) : throw new LessonProviderException();
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token) => throw new NotSupportedException();
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) => throw new NotSupportedException();
    }

    internal static async Task<(Guid Source, Guid Target, Guid Skill)> SeedTransferAsync(IServiceProvider services, string scenario = "valid")
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var user = new UserId(101);
        var sourceUser = scenario == "foreign" ? new(202) : user;
        CurriculumTopic[] topics = [new("basics", "Shared concept", []), new("advanced", "Advanced", ["basics"])];
        var source = LearningProgram.Create(sourceUser, "Algebra", "Beginner", null, "Mathematics", topics,
            scenario == "sourceDiagnosis" ? "Diagnostic question" : null);
        var target = LearningProgram.Create(user, "Mechanics", "Beginner", null,
            scenario == "sameDiscipline" ? "Mathematics" : "Physics", topics, scenario == "diagnosis" ? "Question" : null);
        var sourceSkill = Skill.Create(sourceUser, "Algebra", "Algebraic reasoning");
        var shared = sourceUser == user && scenario != "differentSkill" ? sourceSkill : Skill.Create(user, "Other", "Other concept");
        var lesson = Lesson.Start(sourceUser, "Algebra", "Theory", "Question");
        if (scenario != "sourceDiagnosis") lesson.LinkToProgram(source, "basics");
        lesson.LinkToSkill(sourceSkill);
        lesson.Complete("Answer", "AI interpretation", scenario is not ("sourcePrerequisite" or "sourceUncompleted"));
        db.AddRange(source, target, sourceSkill, lesson, LearningEvidence.FromAnswer(lesson, DateTimeOffset.UtcNow, "Fake AI"), LearningContext.Create(target));
        if (sourceSkill != shared) db.Add(shared);
        if (scenario == "activeLesson") db.Add(Lesson.Start(user, "Existing", "Theory", "Question"));
        await db.SaveChangesAsync();
        return (source.Id, target.Id, shared.Id);
    }

    private static Task Post(HttpClient client, int id, string command) => TelegramCurriculumTests.PostAsync(client, id, 101, command);
}
