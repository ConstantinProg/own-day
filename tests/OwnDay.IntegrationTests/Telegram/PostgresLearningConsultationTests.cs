using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using OwnDay.Application.Dialogue;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Theory]
    [InlineData("Мне нужна помощь с TypeScript generics", LearningIntent.StudyGoal)]
    [InlineData("Хочу заказать пиццу", LearningIntent.Ambiguous)]
    public async Task Post_FreeTextConsultation_CreatesDraftOnlyForStudyGoal(string text, LearningIntent intent)
    {
        var probe = new Probe("") { Intent = intent };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        using var response = await PostPublicAsync(client, 1, text);
        response.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(1);

        await using var db = Open();
        var draft = (await db.Set<DialogueContext>().SingleAsync()).Consultation;
        Assert.Equal(intent == LearningIntent.StudyGoal ? text : null, draft?.OriginalGoal);
        Assert.Empty(await db.Set<LearningProgram>().ToListAsync());
        Assert.Empty(await db.InboxItems.ToListAsync());
        Assert.Empty(await db.Actions.ToListAsync());
        Assert.Equal(1, probe.KindCalls["intent"]);
    }

    [Fact]
    public async Task Post_MarketGoalRestatementAndUnrelatedExperience_StartsExerciseOnResume()
    {
        const string original = "Хочу понять, как работает Московская биржа";
        const string practicalUse = "Хочу понять, как работает биржа, чтобы делать инвистиции";
        var probe = new Probe("")
        {
            Curriculum = new("Рыночная механика", [new("orders", "Биржевые заявки", [])], null),
            Material = new("Лимитная заявка задаёт предельную цену.", "Что ограничивает лимитная заявка?")
        };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        string[] turns = [original, practicalUse, "короткие тексты", "с нуля", "Продолжим"];
        for (var index = 0; index < turns.Length; index++)
        {
            using var response = await PostPublicAsync(client, index + 1, turns[index]);
            response.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(index + 1);
        }

        await using var db = Open();
        var program = await db.Set<LearningProgram>().SingleAsync();
        Assert.Equal(original, program.Goal);
        Assert.Contains(practicalUse, program.Experience);
        Assert.DoesNotContain("короткие тексты", program.Experience);
        Assert.Contains(practicalUse, program.GoalCriterion);
        var lesson = await db.Set<Lesson>().SingleAsync();
        Assert.Equal(probe.Material.Question, lesson.Question);
        var replies = await db.TelegramOutboxMessages.Select(message => message.Text).ToListAsync();
        Assert.Contains(replies, reply => reply.Contains("Биржевые заявки", StringComparison.Ordinal));
        Assert.Contains(replies, reply => reply.Contains(program.GoalCriterion!.Replace("инвистиции", "инвестиции", StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.DoesNotContain(replies, reply => reply.Contains("инвистиции", StringComparison.Ordinal));
        Assert.Contains(replies, reply => reply.Contains(probe.Material.Question, StringComparison.Ordinal));
        await Assert.ThrowsAsync<PostgresException>(() =>
            db.GetService<IMigrator>().MigrateAsync("20261114130000_AddProgramSuspension"));
        db.ChangeTracker.Clear();
        Assert.Equal(program.GoalCriterion, (await db.Set<LearningProgram>().SingleAsync()).GoalCriterion);
    }

    [Theory]
    [InlineData("давай", "пропустить")]
    [InlineData("да", "дальше")]
    [InlineData("давай", "позже")]
    public async Task Post_DiagnosticConsentAndProgressAcrossRestart_PreservesPromptAndOpensOneExercise(string consent, string skip)
    {
        const string question = "Когда проверяется тип значения в TypeScript?";
        var probe = new Probe("")
        {
            Curriculum = new("TypeScript", [new("types", "Статическая проверка типов", [])], question)
        };
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            string[] turns = ["Хочу изучать TypeScript", "Проверять типы в React", "Начинаю с нуля", consent];
            for (var index = 0; index < turns.Length; index++)
            {
                using var response = await PostPublicAsync(client, index + 1, turns[index]);
                response.EnsureSuccessStatusCode();
                await WaitAllPublicAsync(index + 1);
            }
        }

        await using var before = Open();
        var prompt = (await before.Set<DialogueContext>().SingleAsync()).PromptId;
        Assert.Equal(DialogueExpectedResponse.DiagnosticAnswer, (await before.Set<DialogueContext>().SingleAsync()).ExpectedResponse);
        Assert.Contains(await before.TelegramOutboxMessages.ToListAsync(), reply => reply.Text.Contains(question, StringComparison.Ordinal));
        Assert.Empty(await before.Set<Lesson>().ToListAsync());

        using var restarted = ConsultationHost(probe);
        using var restartedClient = restarted.CreateClient();
        using var progress = await PostPublicAsync(restartedClient, 5, "прогресс");
        progress.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(5);
        await before.Entry(await before.Set<DialogueContext>().SingleAsync()).ReloadAsync();
        Assert.Equal(prompt, (await before.Set<DialogueContext>().SingleAsync()).PromptId);

        using var skipped = await PostPublicAsync(restartedClient, 6, skip);
        skipped.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(6);
        using var repeated = await PostPublicAsync(restartedClient, 6, skip);
        repeated.EnsureSuccessStatusCode();
        using var lateConsent = await PostPublicAsync(restartedClient, 7, "давай");
        lateConsent.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(7);

        await using var result = Open();
        var program = await result.Set<LearningProgram>().SingleAsync();
        Assert.False(program.DiagnosticPending);
        Assert.Null(program.DiagnosticAnswer);
        Assert.Single(await result.Set<Lesson>().ToListAsync());
        Assert.Equal(1, probe.KindCalls["plan"]);
        Assert.Equal(1, probe.KindCalls["generate"]);
        Assert.Empty(probe.AssessedAnswers);
    }

    [Fact]
    public async Task Post_EnglishGoalAndExperience_OpensExerciseWithEnglishGuidance()
    {
        var probe = new Probe("")
        {
            Curriculum = new("Geometry", [new("angles", "Triangle angles", [])], null),
            Material = new("The angles of a planar triangle sum to 180 degrees.", "What is the third angle?")
        };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        string[] turns = ["I want to learn geometry", "Calculate angles for a drawing", "No experience", "continue"];
        for (var index = 0; index < turns.Length; index++)
        {
            using var response = await PostPublicAsync(client, index + 1, turns[index]);
            response.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(index + 1);
        }

        await using var db = Open();
        var program = await db.Set<LearningProgram>().SingleAsync();
        Assert.Contains(turns[1], program.GoalCriterion);
        Assert.Equal(probe.Material.Question, (await db.Set<Lesson>().SingleAsync()).Question);
        var replies = await db.TelegramOutboxMessages.Select(message => message.Text).ToListAsync();
        Assert.Contains(replies, reply => reply.Contains("First topics: Triangle angles", StringComparison.Ordinal));
        Assert.Contains(replies, reply => reply.Contains(probe.Material.Theory, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migrate_ConsultationDraft_RefusesDowngradeWithoutLosingSavedGoal()
    {
        var probe = new Probe("");
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            using var goal = await PostPublicAsync(client, 1, "Хочу читать рабочие письма");
            goal.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(1);
        }
        await using var db = Open();
        await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261112200000_AddDialogueContexts"));
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.Equal("Хочу читать рабочие письма", (await db.Set<DialogueContext>().SingleAsync()).Consultation!.OriginalGoal);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Post_UnsupportedConsultationVersion_RejectsBeforeReceiptAndPreservesDraft()
    {
        var probe = new Probe("");
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        using var goal = await PostPublicAsync(client, 1, "Хочу читать рабочие письма");
        goal.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(1);
        await using (var db = Open())
        {
            var version = "{\"version\":99}";
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dialogue_contexts SET consultation = consultation || CAST({version} AS jsonb)");
        }
        using var rejected = await PostPublicAsync(client, 2, "Мои рабочие письма");
        Assert.False(rejected.IsSuccessStatusCode);
        await using var result = Open();
        Assert.Single(await result.Set<OwnDay.Application.Learning.Requests.LearningRequest>().ToListAsync());
        Assert.Equal(99, (await result.Set<DialogueContext>().SingleAsync()).Consultation!.Version);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_ConsultationAcceptedBeforeFlagRollback_RetainsGoalAndRejectsPrematureDetail()
    {
        var probe = new Probe("");
        using (var host = Host(probe, worker: false, publicIngress: true, configure: options =>
            { options.Enabled = true; options.DialogueEnabled = true; options.ConsultationEnabled = true; }))
        using (var client = host.CreateClient())
        {
            using var goal = await PostPublicAsync(client, 1, "Хочу читать рабочие письма");
            goal.EnsureSuccessStatusCode();
            using var premature = await PostPublicAsync(client, 2, "Преждевременный ответ");
            premature.EnsureSuccessStatusCode();
        }
        using var restarted = DialogueHost(probe);
        using var restartClient = restarted.CreateClient();
        await WaitAllPublicAsync(2);
        await using var result = Open();
        var context = await result.Set<DialogueContext>().SingleAsync();
        Assert.Equal("Хочу читать рабочие письма", context.Consultation!.OriginalGoal);
        Assert.Null(context.Consultation.PracticalUse);
        Assert.Equal(DialogueExpectedResponse.ConsultationDetail, context.ExpectedResponse);
        Assert.Empty(await result.InboxItems.ToListAsync());
        Assert.Empty(await result.Set<LearningProgram>().ToListAsync());
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Migrate_ExistingExerciseConfirmation_PreservesPendingConsentAndPedagogicalState()
    {
        await SeedAsync("learn_ask");
        var probe = new Probe("");
        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient())
        {
            using var finish = await PostPublicAsync(client, 1, "/learn_finish");
            finish.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(1);
        }
        await using var db = Open();
        var before = await db.Set<DialogueContext>().AsNoTracking().SingleAsync();
        // Simulate the pre-assistance schema before testing its populated upgrade.
        await db.Database.ExecuteSqlRawAsync("UPDATE learning_lessons SET \"Assistance\" = 0");
        await db.GetService<IMigrator>().MigrateAsync("20261112200000_AddDialogueContexts");
        await db.Database.MigrateAsync();
        var after = await db.Set<DialogueContext>().AsNoTracking().SingleAsync();
        Assert.Equal(DialogueContext.CurrentSchemaVersion, after.SchemaVersion);
        Assert.Equal(before.PendingConfirmation, after.PendingConfirmation);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Null(after.Consultation);
        Assert.Equal(LessonStatus.AwaitingAnswer, (await db.Set<Lesson>().SingleAsync()).Status);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_ConsultationPlannerFails_PreservesDraftAndExplicitRetryCreatesOnce()
    {
        var failed = new Probe("") { FailureKind = "plan" };
        using (var host = ConsultationHost(failed))
        using (var client = host.CreateClient())
        {
            using var goal = await PostPublicAsync(client, 1, "Хочу читать рабочие письма");
            goal.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(1);
            using var usage = await PostPublicAsync(client, 2, "Переписка с коллегами");
            usage.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(2);
            using var experience = await PostPublicAsync(client, 3, "Начинаю с нуля");
            experience.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(3);
            await using var db = Open();
            Assert.Empty(await db.Set<LearningProgram>().ToListAsync());
            Assert.Equal("Начинаю с нуля", (await db.Set<DialogueContext>().SingleAsync()).Consultation!.Experience);
        }
        var probe = new Probe("");
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            using var retry = await PostPublicAsync(client, 4, "повторить");
            retry.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(4);
        }
        await using var result = Open();
        Assert.Equal("Хочу читать рабочие письма", (await result.Set<LearningProgram>().SingleAsync()).Goal);
        Assert.Equal(1, probe.KindCalls["plan"]);
        Assert.Empty(await result.InboxItems.ToListAsync());
    }

    [Fact]
    public async Task Post_StartAndLegacyCaptureDuringConsultation_PreservesDraftAcrossRestart()
    {
        var probe = new Probe("");
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            using var goal = await PostPublicAsync(client, 1, "Хочу читать и писать на английском");
            goal.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(1);
            using var start = await PostPublicAsync(client, 2, "/start");
            start.EnsureSuccessStatusCode();
            using var capture = await PostPublicAsync(client, 3, "/capture A separate note");
            capture.EnsureSuccessStatusCode();
        }
        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient())
        {
            using var resume = await PostPublicAsync(client, 4, "Продолжим");
            resume.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(2);
            using var replacement = await PostPublicAsync(client, 5, "Хочу изучать другую дисциплину");
            replacement.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(3);
        }
        await using var result = Open();
        var draft = (await result.Set<DialogueContext>().SingleAsync()).Consultation!;
        Assert.Equal("Хочу читать и писать на английском", draft.OriginalGoal);
        Assert.Null(draft.PracticalUse);
        Assert.Empty(await result.InboxItems.ToListAsync());
        Assert.Empty(await result.Set<LearningProgram>().ToListAsync());
        Assert.Equal(1, await result.TelegramOutboxMessages.CountAsync(message => message.Text == OwnDay.Infrastructure.Telegram.Commands.TelegramDialoguePresenter.Introduction));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_ConsultationWithExistingProgram_DoesNotReplaceItOrCallPlanner()
    {
        await SeedAsync("learn_request");
        var probe = new Probe("");
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        using var goal = await PostPublicAsync(client, 1, "Хочу читать и писать на английском");
        goal.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(1);
        await using var result = Open();
        Assert.Equal("Work emails", (await result.Set<LearningProgram>().SingleAsync()).Goal);
        Assert.Null((await result.Set<DialogueContext>().SingleAsync()).Consultation);
        Assert.Empty(await result.InboxItems.ToListAsync());
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_FirstGoalAcrossRestart_CreatesOneUnlinkedProgramAndPreservesOriginalWish()
    {
        const string goal = "Хочу читать и писать на английском";
        var probe = new Probe("");
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            using var first = await PostPublicAsync(client, 1, goal);
            first.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(1);
            await using var observer = Open();
            Assert.Empty(await observer.Set<LearningProgram>().ToListAsync());
            Assert.Contains(await observer.TelegramOutboxMessages.ToListAsync(), message => message.Text.Contains("Для чего", StringComparison.Ordinal));
        }
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            using var usage = await PostPublicAsync(client, 2, "Рабочие письма");
            usage.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(2);
            using var experience = await PostPublicAsync(client, 3, "b1");
            experience.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(3);
            using var repeated = await PostPublicAsync(client, 3, "b1");
            repeated.EnsureSuccessStatusCode();
        }
        await using var result = Open();
        var program = await result.Set<LearningProgram>().SingleAsync();
        Assert.Equal(goal, program.Goal);
        Assert.Null(program.ProjectId);
        Assert.Contains("Рабочие письма", program.Experience);
        Assert.Contains("Рабочие письма", program.GoalCriterion);
        Assert.Contains("b1", program.Experience);
        Assert.Equal(DialoguePhase.DiagnosticOffer, (await result.Set<DialogueContext>().SingleAsync()).Phase);
        Assert.Empty(await result.InboxItems.ToListAsync());
        Assert.Empty(await result.Projects.ToListAsync());
        Assert.Empty(await result.Actions.ToListAsync());
        Assert.Equal(0, probe.Calls);
    }

    private WebApplicationFactory<Program> ConsultationHost(Probe probe) => DialogueHost(probe).WithWebHostBuilder(builder =>
        builder.UseSetting("Learning:Processing:ConsultationEnabled", "true"));
}
