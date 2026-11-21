using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Dialogue;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_TwoExercisesAcrossRestart_ContinuesAutomaticallyAndStopsAtRequestedCount()
    {
        var probe = new Probe("")
        {
            Recognize = input => input.Input switch
            {
                "Можем перейти к следующему?" => LearningIntent.Next,
                "Готов снова заниматься" => LearningIntent.Resume,
                _ => LearningIntent.Answer
            }
        };
        using (var host = PracticeHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
            await SendEnglishAsync(client, 2, "Рабочие письма");
            await SendEnglishAsync(client, 3, "Начинаю с нуля");
            await SendEnglishAsync(client, 4, "пропустим");
            await SendEnglishAsync(client, 5, "давай два упражнения");
            await SendEnglishAsync(client, 6, "К упражнению");
            await SendEnglishAsync(client, 7, "Kim asks for the updated agenda. I can send it tomorrow.");
            await using var db = Open();
            Assert.Equal(2, await db.Set<Lesson>().CountAsync());
            Assert.Single(await db.Set<LearningEvidence>().ToListAsync());
        }
        using (var restarted = PracticeHost(probe))
        using (var client = restarted.CreateClient())
        {
            await SendEnglishAsync(client, 8, "пауза");
            await SendEnglishAsync(client, 9, "Можем перейти к следующему?");
            await using (var db = Open())
            {
                Assert.Equal(2, await db.Set<Lesson>().CountAsync());
                Assert.Equal(LearningPracticeStatus.Paused, (await db.Set<LearningContext>().SingleAsync()).Practice!.Status);
            }
            await SendEnglishAsync(client, 10, "Готов снова заниматься");
            await SendEnglishAsync(client, 11, "A second answer.");
            await SendEnglishAsync(client, 12, "дальше");
            using var duplicate = await PostPublicAsync(client, 12, "дальше");
            duplicate.EnsureSuccessStatusCode();
        }
        await using var result = Open();
        var lessons = await result.Set<Lesson>().ToListAsync();
        Assert.Equal(2, lessons.Count);
        Assert.All(lessons, lesson => Assert.Equal(LessonStatus.Completed, lesson.Status));
        Assert.Single(lessons.Select(lesson => lesson.SkillId).Distinct());
        Assert.Equal(2, await result.Set<LearningEvidence>().CountAsync());
        Assert.Equal(1, probe.KindCalls.GetValueOrDefault("generate"));
        Assert.Empty(await result.InboxItems.ToListAsync());
        Assert.Equal(LearningPracticeStatus.Completed, (await result.Set<LearningContext>().SingleAsync()).Practice!.Status);
        Assert.False(result.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Post_PauseStopAndFlagRollback_KeepsUnfinishedQuestionWithoutEvidence()
    {
        var probe = new Probe("");
        Guid original;
        using (var host = PracticeHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
            await SendEnglishAsync(client, 2, "Рабочие письма");
            await SendEnglishAsync(client, 3, "Начинаю с нуля");
            await SendEnglishAsync(client, 4, "пропустим");
            await SendEnglishAsync(client, 5, "давай три упражнения");
            await SendEnglishAsync(client, 6, "пауза");
            await using var db = Open();
            original = (await db.Set<Lesson>().SingleAsync()).Id;
            Assert.Equal(LearningPracticeStatus.Paused, (await db.Set<LearningContext>().SingleAsync()).Practice!.Status);
        }
        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 7, "Продолжим");
            await SendEnglishAsync(client, 8, "хватит практики");
        }
        await using var result = Open();
        var lesson = await result.Set<Lesson>().SingleAsync();
        Assert.Equal(original, lesson.Id);
        Assert.Equal(LessonStatus.Paused, lesson.Status);
        Assert.Null(lesson.Answer);
        Assert.Equal(LearningPracticeStatus.Stopped, (await result.Set<LearningContext>().SingleAsync()).Practice!.Status);
        Assert.Empty(await result.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(0, probe.Calls);
        await Assert.ThrowsAsync<PostgresException>(() => result.GetService<IMigrator>().MigrateAsync("20261114090000_AddEnglishDiagnostic"));
    }

    [Fact]
    public async Task Post_GenerationFailure_DoesNotConsumeExerciseAndExplicitNextRetriesOnce()
    {
        var probe = new Probe("") { FailureKind = "generate" };
        using (var host = PracticeHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
            await SendEnglishAsync(client, 2, "Рабочие письма");
            await SendEnglishAsync(client, 3, "Начинаю с нуля");
            await SendEnglishAsync(client, 4, "пропустим");
            await SendEnglishAsync(client, 5, "давай два упражнения");
            await SendEnglishAsync(client, 6, "К упражнению");
            await SendEnglishAsync(client, 7, "Kim asks for the agenda. I can send it tomorrow.");
            await SendEnglishAsync(client, 8, "дальше");
        }
        await using (var db = Open())
        {
            Assert.Single(await db.Set<Lesson>().ToListAsync());
            Assert.Single((await db.Set<LearningContext>().SingleAsync()).Practice!.LessonIds);
        }
        var recovery = new Probe("");
        using (var host = PracticeHost(recovery))
        using (var client = host.CreateClient()) await SendEnglishAsync(client, 9, "дальше");
        await using var result = Open();
        Assert.Equal(2, await result.Set<Lesson>().CountAsync());
        Assert.Equal(2, (await result.Set<LearningContext>().SingleAsync()).Practice!.LessonIds.Length);
        Assert.Equal(1, recovery.KindCalls.GetValueOrDefault("generate"));
        Assert.Single(await result.Set<LearningEvidence>().ToListAsync());
    }

    private WebApplicationFactory<Program> PracticeHost(Probe probe) => EnglishHost(probe).WithWebHostBuilder(builder =>
        builder.UseSetting("Learning:Processing:PracticeEnabled", "true"));

    [Fact]
    public async Task Post_LateNextOlderThanCompletedAnswer_DoesNotGenerateAnotherExercise()
    {
        var probe = new Probe("");
        using var host = PracticeHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
        await SendEnglishAsync(client, 2, "Рабочие письма");
        await SendEnglishAsync(client, 3, "Начинаю с нуля");
        await SendEnglishAsync(client, 4, "пропустим");
        await SendEnglishAsync(client, 5, "давай два упражнения");
        await SendEnglishAsync(client, 6, "К упражнению");
        using var answer = await PostPublicAsync(client, 101, "Kim asks for the agenda. I can send it tomorrow.");
        answer.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(7);
        using var late = await PostPublicAsync(client, 7, "дальше");
        late.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(8);
        await using (var db = Open())
        {
            Assert.Equal(2, await db.Set<Lesson>().CountAsync());
            Assert.Equal(2, (await db.Set<LearningContext>().SingleAsync()).Practice!.LessonIds.Length);
        }
        Assert.Equal(1, probe.KindCalls.GetValueOrDefault("generate"));
        using var fresh = await PostPublicAsync(client, 102, "дальше");
        fresh.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(9);
        await using var result = Open();
        Assert.Equal(2, await result.Set<Lesson>().CountAsync());
        Assert.Equal(1, probe.KindCalls.GetValueOrDefault("generate"));
    }

    [Fact]
    public async Task Post_OtherLessonOrInvalidVolumeDuringPractice_DoesNotReplaceBoundedSkill()
    {
        var probe = new Probe("");
        using var host = PracticeHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
        await SendEnglishAsync(client, 2, "Рабочие письма");
        await SendEnglishAsync(client, 3, "Начинаю с нуля");
        await SendEnglishAsync(client, 4, "пропустим");
        await SendEnglishAsync(client, 5, "давай два упражнения");
        await SendEnglishAsync(client, 6, "К упражнению");
        await SendEnglishAsync(client, 7, "Kim asks for the agenda. I can send it tomorrow.");
        await SendEnglishAsync(client, 8, "/learn A different skill");
        await SendEnglishAsync(client, 9, "практика 4");
        await SendEnglishAsync(client, 10, "помощь");
        await using var result = Open();
        Assert.Equal(2, await result.Set<Lesson>().CountAsync());
        var practice = (await result.Set<LearningContext>().SingleAsync()).Practice!;
        Assert.Equal(2, practice.LessonIds.Length);
        Assert.Equal(2, practice.RequestedCount);
        Assert.Equal(LearningPracticeStatus.Running, practice.Status);
        Assert.Equal(1, probe.KindCalls.GetValueOrDefault("generate"));
    }

    [Fact]
    public async Task Post_FirstPracticeReceiptBeforeFlagRollback_RetainsAcceptedCapability()
    {
        var probe = new Probe("");
        using (var host = PracticeHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
            await SendEnglishAsync(client, 2, "Рабочие письма");
            await SendEnglishAsync(client, 3, "Начинаю с нуля");
            await SendEnglishAsync(client, 4, "пропустим");
        }
        using (var host = Host(probe, worker: false, publicIngress: true, configure: options =>
            { options.Enabled = true; options.DialogueEnabled = true; options.PracticeEnabled = true; }))
        using (var client = host.CreateClient())
        {
            using var receipt = await PostPublicAsync(client, 5, "давай два упражнения");
            receipt.EnsureSuccessStatusCode();
        }
        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient()) await WaitAllPublicAsync(5);
        await using var result = Open();
        var practice = (await result.Set<LearningContext>().SingleAsync()).Practice!;
        Assert.Single(practice.LessonIds);
        Assert.Equal(2, practice.RequestedCount);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_UnknownPracticeVersion_RejectsBeforeReceiptWithoutRewritingQuestion()
    {
        var probe = new Probe("");
        using var host = PracticeHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
        await SendEnglishAsync(client, 2, "Рабочие письма");
        await SendEnglishAsync(client, 3, "Начинаю с нуля");
        await SendEnglishAsync(client, 4, "пропустим");
        await using (var db = Open())
        {
            var unknown = "{\"version\":99}";
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE learning_contexts SET \"Practice\" = CAST({unknown} AS jsonb)");
        }
        using var rejected = await PostPublicAsync(client, 5, "давай два упражнения");
        Assert.False(rejected.IsSuccessStatusCode);
        await using var result = Open();
        Assert.Equal(4, await result.Set<LearningRequest>().CountAsync());
        Assert.Equal(99, (await result.Set<LearningContext>().SingleAsync()).Practice!.Version);
        Assert.Equal(LessonStatus.AwaitingAnswer, (await result.Set<Lesson>().SingleAsync()).Status);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_PracticeAfterCompletedRoute_PracticesActualOwnedSkillAndDoesNotRestartRoute()
    {
        var probe = new Probe("");
        using var host = PracticeHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
        await SendEnglishAsync(client, 2, "Рабочие письма");
        await SendEnglishAsync(client, 3, "Начинаю с нуля");
        await SendEnglishAsync(client, 4, "пропустим");
        await SendEnglishAsync(client, 5, "К упражнению");
        await SendEnglishAsync(client, 6, "Kim asks for the agenda. I can send it tomorrow.");
        await SendEnglishAsync(client, 7, "давай одно упражнение");
        await SendEnglishAsync(client, 8, "A fresh practice answer.");
        await SendEnglishAsync(client, 9, "дальше");
        await using var result = Open();
        var lessons = await result.Set<Lesson>().ToListAsync();
        Assert.Equal(2, lessons.Count);
        Assert.Single(lessons.Select(lesson => lesson.SkillId).Distinct());
        Assert.Equal(LearningPracticeStatus.Completed, (await result.Set<LearningContext>().SingleAsync()).Practice!.Status);
        Assert.Single((await result.Set<LearningContext>().SingleAsync()).Practice!.LessonIds);
        Assert.Equal(1, probe.KindCalls.GetValueOrDefault("generate"));
        Assert.Single((await result.Set<LearningProgram>().Include(program => program.Routes).SingleAsync()).Routes);
    }

    [Fact]
    public async Task Process_AnswerAndFollowUpApplyFails_ReplaysSavedAssessmentAndAssociatesOneExercise()
    {
        var probe = new Probe("");
        using (var host = PracticeHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
            await SendEnglishAsync(client, 2, "Рабочие письма");
            await SendEnglishAsync(client, 3, "Начинаю с нуля");
            await SendEnglishAsync(client, 4, "пропустим");
            await SendEnglishAsync(client, 5, "давай два упражнения");
            await SendEnglishAsync(client, 6, "К упражнению");
        }
        using (var host = Host(probe, worker: false, publicIngress: true, interceptor: new RejectBoundary("apply"),
            configure: options => { options.Enabled = true; options.DialogueEnabled = true; }))
        using (var client = host.CreateClient())
        {
            using var receipt = await PostPublicAsync(client, 7, "Kim asks for the agenda. I can send it tomorrow.");
            receipt.EnsureSuccessStatusCode();
            using var scope = host.Services.CreateScope();
            var lease = (await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        }
        await using (var db = Open())
        {
            Assert.Single(await db.Set<Lesson>().ToListAsync());
            Assert.Single((await db.Set<LearningContext>().SingleAsync()).Practice!.LessonIds);
            Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
            await db.Database.ExecuteSqlRawAsync("UPDATE learning_requests SET lease_until = clock_timestamp() - interval '1 second' WHERE status = 2");
        }
        using (var host = PracticeHost(probe))
        using (var client = host.CreateClient()) await WaitAllPublicAsync(7);
        await using var result = Open();
        Assert.Equal(2, await result.Set<Lesson>().CountAsync());
        Assert.Equal(2, (await result.Set<LearningContext>().SingleAsync()).Practice!.LessonIds.Length);
        Assert.Single(await result.Set<LearningEvidence>().ToListAsync());
        Assert.Single(probe.AssessedAnswers);
        Assert.Equal(1, probe.KindCalls.GetValueOrDefault("generate"));
    }
}
