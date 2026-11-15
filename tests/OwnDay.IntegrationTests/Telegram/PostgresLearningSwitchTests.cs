using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using OwnDay.Application.Learning.Requests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_SwitchWithCurrentQuestion_PublishesConsentWithoutChangingLesson()
    {
        await SeedAsync("learn_ask");
        Guid target;
        Guid original;
        await using (var db = Open())
        {
            original = (await db.Set<Lesson>().SingleAsync()).Id;
            var program = LearningProgram.Create(_user, "Read another email", "Beginner", null, "English", [new("root", "Read email", [])], null);
            target = program.Id;
            db.Add(program);
            await db.SaveChangesAsync();
        }
        var probe = new Probe("");
        using var host = SwitchHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "/learn_select " + target);
        await using var result = Open();
        var proposal = (await result.Set<DialogueContext>().SingleAsync()).PendingConfirmation;
        Assert.NotNull(proposal);
        Assert.Equal(target, proposal.Target!.ProgramId);
        Assert.Equal(LessonStatus.AwaitingAnswer, (await result.Set<Lesson>().SingleAsync()).Status);
        Assert.Equal(original, proposal.Target.LessonId);
        Assert.Equal(0, probe.Calls);
    }

    private WebApplicationFactory<Program> SwitchHost(Probe probe) => DialogueHost(probe).WithWebHostBuilder(builder =>
        builder.UseSetting("Learning:Processing:ProgramSwitchEnabled", "true"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_ConfirmedSwitchAndReturnAcrossRestart_RestoresQuestionAndOriginalEvidenceTarget(bool paused)
    {
        await SeedAsync("learn_ask");
        Guid source;
        Guid target;
        Guid question;
        await using (var db = Open())
        {
            var program = await db.Set<LearningProgram>().Include(value => value.Routes).SingleAsync();
            source = program.Id;
            var lesson = await db.Set<Lesson>().SingleAsync();
            lesson.LinkToProgram(program, "root");
            lesson.AddExplanation(LessonInteractionKind.Clarification, "Why?", "Saved help");
            if (paused) lesson.Pause();
            question = lesson.Id;
            var other = LearningProgram.Create(_user, "Another goal", "Beginner", null, "English", [new("root", "Another topic", [])], null);
            target = other.Id;
            db.Add(other);
            await db.SaveChangesAsync();
        }
        var probe = new Probe("");
        string returnCode;
        using (var host = SwitchHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "выбрать программу " + target);
            var code = await SwitchCodeAsync();
            await SendEnglishAsync(client, 2, "да");
            Assert.Equal(code, await SwitchCodeAsync());
            await SendEnglishAsync(client, 3, "подтверждаю " + code);
            await using (var db = Open())
            {
                Assert.Equal(target, (await db.Set<LearningContext>().SingleAsync()).ProgramId);
                Assert.Equal(LessonStatus.Suspended, (await db.Set<Lesson>().SingleAsync()).Status);
                Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
                Assert.False(db.Database.HasPendingModelChanges());
            }
            await SendEnglishAsync(client, 4, "/learn_next");
            await SendEnglishAsync(client, 5, "пауза");
            await SendEnglishAsync(client, 6, "выбрать программу " + source);
            returnCode = await SwitchCodeAsync();
        }
        using (var host = DialogueHost(probe, enabled: false))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 7, "подтверждаю " + returnCode);
            await using (var db = Open())
            {
                var restored = await db.Set<Lesson>().SingleAsync(value => value.Id == question);
                Assert.Equal(paused ? LessonStatus.Paused : LessonStatus.AwaitingAnswer, restored.Status);
                Assert.Equal("What is requested?", restored.Question);
                Assert.Equal("Saved help", (await db.Set<LessonInteraction>().SingleAsync()).Explanation);
                Assert.Null(restored.SuspendedFrom);
                Assert.Equal(question, (await db.Set<DialogueContext>().SingleAsync()).TargetLessonId);
            }
            await SendEnglishAsync(client, 8, "подтверждаю " + returnCode);
            await SendEnglishAsync(client, 9, "Продолжим");
            await SendEnglishAsync(client, 10, "A fresh answer");
        }
        await using var result = Open();
        Assert.Equal(source, (await result.Set<LearningContext>().SingleAsync()).ProgramId);
        var evidence = await result.Set<LearningEvidence>().SingleAsync();
        Assert.Equal(source, (await result.Set<Lesson>().SingleAsync(value => value.Id == evidence.LessonId)).ProgramId);
        Assert.Equal(question, evidence.LessonId);
        Assert.Equal(AnswerAssistance.Assisted, evidence.Assistance);
        var savedOther = await result.Set<Lesson>().SingleAsync(value => value.ProgramId == target);
        Assert.Equal(LessonStatus.Suspended, savedOther.Status);
        Assert.Equal(LessonStatus.Paused, savedOther.SuspendedFrom);
        Assert.Equal(2, await result.Set<Lesson>().CountAsync());
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("expired")]
    [InlineData("target")]
    public async Task Post_InvalidatedSwitchConsent_KeepsOriginalSelectionAndQuestion(string change)
    {
        await SeedAsync("learn_ask");
        Guid target;
        Guid source;
        await using (var db = Open())
        {
            source = (await db.Set<LearningContext>().SingleAsync()).ProgramId;
            var other = LearningProgram.Create(_user, "Other", "Beginner", null, "English", [new("root", "Other", [])], null);
            target = other.Id;
            db.Add(other);
            await db.SaveChangesAsync();
        }
        var probe = new Probe("");
        using var host = SwitchHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "/learn_select " + target);
        var code = await SwitchCodeAsync();
        if (change is "pause" or "resume") await SendEnglishAsync(client, 2, change == "pause" ? "пауза" : "Продолжим");
        else
        {
            await using var db = Open();
            if (change == "target") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE learning_programs SET \"Experience\" = \"Experience\" WHERE \"Id\" = {target}");
            else await db.Database.ExecuteSqlRawAsync("UPDATE dialogue_contexts SET pending_confirmation = jsonb_set(pending_confirmation, ARRAY['expiresAt'], to_jsonb('2000-01-01T00:00:00+00:00'::text))");
        }
        var id = change is "pause" or "resume" ? 3 : 2;
        await SendEnglishAsync(client, id, "подтверждаю " + code);
        await using var result = Open();
        Assert.Equal(source, (await result.Set<LearningContext>().SingleAsync()).ProgramId);
        Assert.Equal(change == "pause" ? LessonStatus.Paused : LessonStatus.AwaitingAnswer, (await result.Set<Lesson>().SingleAsync()).Status);
        Assert.Empty(await result.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(0, probe.Calls);
    }

    private async Task<string> SwitchCodeAsync()
    {
        await using var db = Open();
        return Assert.IsType<DialogueConfirmation>((await db.Set<DialogueContext>().SingleAsync()).PendingConfirmation).Code;
    }

    [Fact]
    public async Task Migrate_LegacyProgramAndQuestion_PreservesReturnableStateWithoutInventingSuspension()
    {
        await SeedAsync("learn_ask");
        await using var db = Open();
        var question = await db.Set<Lesson>().SingleAsync();
        question.AddExplanation(LessonInteractionKind.Clarification, "Why?", "Preserved help");
        question.Pause();
        await db.SaveChangesAsync();
        var id = question.Id;
        var program = (await db.Set<LearningContext>().SingleAsync()).ProgramId;
        await db.GetService<IMigrator>().MigrateAsync("20261114110000_AddBoundedLearningPractice");
        db.ChangeTracker.Clear();
        await db.Database.MigrateAsync();
        var preserved = await db.Set<Lesson>().Include(value => value.Interactions).SingleAsync();
        Assert.Equal(id, preserved.Id);
        Assert.Equal(LessonStatus.Paused, preserved.Status);
        Assert.Null(preserved.SuspendedFrom);
        Assert.Null(preserved.SuspendedForProgramId);
        Assert.Equal("Preserved help", Assert.Single(preserved.Interactions).Explanation);
        Assert.Equal(program, (await db.Set<LearningContext>().SingleAsync()).ProgramId);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Post_ForeignProgramSwitch_DoesNotPublishConsentOrSuspendCurrentQuestion()
    {
        await SeedAsync("learn_ask");
        Guid foreign;
        await using (var db = Open())
        {
            var program = LearningProgram.Create(new(202), "Private goal", "Private experience", null, "English", [new("root", "Private topic", [])], null);
            foreign = program.Id;
            db.Add(program);
            await db.SaveChangesAsync();
        }
        using var host = SwitchHost(new(""));
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "выбрать программу " + foreign);
        await using var result = Open();
        Assert.Null((await result.Set<DialogueContext>().SingleAsync()).PendingConfirmation);
        Assert.Equal(LessonStatus.AwaitingAnswer, (await result.Set<Lesson>().SingleAsync()).Status);
        Assert.DoesNotContain("Private", string.Join(" ", await result.TelegramOutboxMessages.Select(value => value.Text).ToArrayAsync()));
    }

    [Fact]
    public async Task Post_SuspendedDestinationChanges_RejectsPreviouslyIssuedReturnCode()
    {
        await SeedAsync("learn_ask");
        Guid source;
        Guid target;
        await using (var db = Open())
        {
            source = (await db.Set<LearningContext>().SingleAsync()).ProgramId;
            var other = LearningProgram.Create(_user, "Other", "Beginner", null, "English", [new("root", "Other", [])], null);
            target = other.Id;
            db.Add(other);
            await db.SaveChangesAsync();
        }
        using var host = SwitchHost(new(""));
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "/learn_select " + target);
        await SendEnglishAsync(client, 2, "подтверждаю " + await SwitchCodeAsync());
        await SendEnglishAsync(client, 3, "/learn_select " + source);
        var code = await SwitchCodeAsync();
        await using (var db = Open())
            await db.Database.ExecuteSqlRawAsync("UPDATE learning_lessons SET \"Theory\" = \"Theory\" WHERE \"Status\" = 4");
        await SendEnglishAsync(client, 4, "подтверждаю " + code);
        await using var result = Open();
        Assert.Equal(target, (await result.Set<LearningContext>().SingleAsync()).ProgramId);
        Assert.Equal(LessonStatus.Suspended, (await result.Set<Lesson>().SingleAsync()).Status);
        Assert.Null((await result.Set<DialogueContext>().SingleAsync()).PendingConfirmation);
    }

    [Fact]
    public async Task Post_CommandAcceptedBeforeFlagRollback_RetainsSwitchProposalCapability()
    {
        await SeedAsync("learn_ask");
        Guid target;
        await using (var db = Open())
        {
            var other = LearningProgram.Create(_user, "Other", "Beginner", null, "English", [new("root", "Other", [])], null);
            target = other.Id;
            db.Add(other);
            await db.SaveChangesAsync();
        }
        var probe = new Probe("");
        using (var host = Host(probe, worker: false, publicIngress: true,
            configure: options => { options.Enabled = true; options.DialogueEnabled = true; options.ProgramSwitchEnabled = true; }))
        using (var client = host.CreateClient())
        {
            using var accepted = await PostPublicAsync(client, 1, "/learn_select " + target);
            accepted.EnsureSuccessStatusCode();
        }
        using var restarted = DialogueHost(probe, enabled: false);
        using var restartedClient = restarted.CreateClient();
        await WaitAllPublicAsync(1);
        await using var result = Open();
        Assert.Equal(DialogueOperationKind.SwitchProgram, (await result.Set<DialogueContext>().SingleAsync()).PendingConfirmation!.Operation);
        Assert.Equal(LessonStatus.AwaitingAnswer, (await result.Set<Lesson>().SingleAsync()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_ConfirmedNewGoal_PreservesOldProgramOrLeavesItUntouchedOnProviderFailure(bool fail)
    {
        await SeedAsync("learn_ask");
        Guid original;
        string originalGoal;
        await using (var db = Open())
        {
            var old = await db.Set<LearningProgram>().SingleAsync();
            original = old.Id;
            originalGoal = old.Goal;
        }
        var probe = new Probe("") { FailureKind = fail ? "plan" : null, FailureCount = 10 };
        using var host = SwitchHost(probe);
        using var client = host.CreateClient();
        const string newGoal = "Научиться решать линейные уравнения";
        await SendEnglishAsync(client, 1, "сменить цель на " + newGoal);
        var code = await SwitchCodeAsync();
        Assert.Equal(0, probe.Calls);
        await SendEnglishAsync(client, 2, "подтверждаю " + code);
        await using var result = Open();
        Assert.Equal(originalGoal, (await result.Set<LearningProgram>().SingleAsync(value => value.Id == original)).Goal);
        var selected = (await result.Set<LearningContext>().SingleAsync()).ProgramId;
        Assert.Equal(fail, selected == original);
        Assert.Equal(fail ? LessonStatus.AwaitingAnswer : LessonStatus.Suspended, (await result.Set<Lesson>().SingleAsync()).Status);
        Assert.Equal(fail ? 1 : 2, await result.Set<LearningProgram>().CountAsync());
        Assert.Empty(await result.Set<LearningEvidence>().ToListAsync());
        if (!fail) Assert.Equal(newGoal, (await result.Set<LearningProgram>().SingleAsync(value => value.Id == selected)).Goal);
        Assert.DoesNotContain(await result.Set<LearningRequest>().ToListAsync(), request => request.Outcome == "stale_target");
    }

    [Fact]
    public async Task Process_SwitchApplyCommitFails_RollsBackSuspensionSelectionAndConsentThenRecovers()
    {
        await SeedAsync("learn_ask");
        Guid original;
        var probe = new Probe("");
        string code;
        using (var host = SwitchHost(probe))
        using (var client = host.CreateClient())
        {
            await using var db = Open();
            original = (await db.Set<LearningContext>().SingleAsync()).ProgramId;
            await SendEnglishAsync(client, 1, "сменить цель на Solve equations");
            code = await SwitchCodeAsync();
        }
        using (var host = Host(probe, worker: false, publicIngress: true, interceptor: new RejectBoundary("apply"),
            configure: options => { options.Enabled = true; options.DialogueEnabled = true; }))
        using (var client = host.CreateClient())
        {
            using var accepted = await PostPublicAsync(client, 2, "подтверждаю " + code);
            accepted.EnsureSuccessStatusCode();
            using var scope = host.Services.CreateScope();
            var lease = (await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        }
        await using (var db = Open())
        {
            Assert.Equal(original, (await db.Set<LearningContext>().SingleAsync()).ProgramId);
            Assert.Single(await db.Set<LearningProgram>().ToListAsync());
            Assert.Equal(LessonStatus.AwaitingAnswer, (await db.Set<Lesson>().SingleAsync()).Status);
            Assert.NotNull((await db.Set<DialogueContext>().SingleAsync()).PendingConfirmation);
            await db.Database.ExecuteSqlRawAsync("UPDATE learning_requests SET lease_until = clock_timestamp() - interval '1 second' WHERE status = 2");
        }
        using var restarted = DialogueHost(probe, enabled: false);
        using var restartedClient = restarted.CreateClient();
        await WaitAllPublicAsync(2);
        await using var result = Open();
        Assert.NotEqual(original, (await result.Set<LearningContext>().SingleAsync()).ProgramId);
        Assert.Equal(LessonStatus.Suspended, (await result.Set<Lesson>().SingleAsync()).Status);
        Assert.Null((await result.Set<DialogueContext>().SingleAsync()).PendingConfirmation);
        Assert.Equal(1, probe.KindCalls.GetValueOrDefault("plan"));
        await Assert.ThrowsAsync<PostgresException>(() => result.GetService<IMigrator>().MigrateAsync("20261114110000_AddBoundedLearningPractice"));
    }
}
