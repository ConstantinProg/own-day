using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Infrastructure.Learning;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_EnglishDiagnosticCompleteOrSkip_StartsOneLessonWithoutInbox(bool complete)
    {
        var probe = new Probe("");
        using var host = EnglishHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
        await SendEnglishAsync(client, 2, "Рабочие письма");
        await SendEnglishAsync(client, 3, "Понимаю отдельные слова");
        if (complete)
        {
            await SendEnglishAsync(client, 4, "давай");
            await SendEnglishAsync(client, 5, "B");
            await SendEnglishAsync(client, 6, "ответ: Yes, I can send the list today.");
        }
        else await SendEnglishAsync(client, 4, "пропустим");

        // The alternative diagnostic command cannot reset an already started Lesson.
        await SendEnglishAsync(client, complete ? 7 : 5, "/learn_diagnose skip");

        await using var db = Open();
        var program = await db.Set<LearningProgram>().Include(value => value.Routes).SingleAsync();
        var lesson = await db.Set<Lesson>().SingleAsync();
        Assert.False(program.DiagnosticPending);
        Assert.Equal(complete ? EnglishDiagnosticStatus.Completed : EnglishDiagnosticStatus.Skipped, program.EnglishDiagnostic!.Status);
        Assert.Equal(complete ? 2 : 0, program.EnglishDiagnostic.Results.Length);
        Assert.Equal(complete ? 2 : 1, program.CurrentRoute.Version);
        Assert.Equal("Хочу читать и писать на английском", program.Goal);
        Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
        Assert.Equal(program.Id, lesson.ProgramId);
        Assert.Equal(EnglishLearningContent.FirstLesson, lesson.Content);
        Assert.Null(lesson.ExpectedAnswer);
        Assert.True(lesson.ExercisePending);
        Assert.Equal(DialogueExpectedResponse.None, (await db.Set<DialogueContext>().SingleAsync()).ExpectedResponse);
        Assert.Empty(await db.InboxItems.ToListAsync());
        Assert.Empty(await db.Projects.ToListAsync());
        Assert.Empty(await db.Actions.ToListAsync());
        Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(0, probe.KindCalls.GetValueOrDefault("generate"));
        if (complete)
        {
            Assert.Equal(EnglishLearningContent.Writing, Assert.Single(probe.AssessedLessons).Content);
            Assert.Equal(0, probe.KindCalls.GetValueOrDefault("intent"));
        }
    }

    [Fact]
    public async Task Post_DiagnosticRestartHelpAndSkip_KeepsEachItemAndAssistedLessonEvidence()
    {
        var probe = new Probe("") { Intent = OwnDay.Application.Dialogue.LearningIntent.Clarify };
        using (var host = EnglishHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
            await SendEnglishAsync(client, 2, "Рабочие письма");
            await SendEnglishAsync(client, 3, "Начинаю с нуля");
            await SendEnglishAsync(client, 4, "начнём диагностику");
        }
        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 5, "Продолжим");
            await SendEnglishAsync(client, 6, "помощь");
            await SendEnglishAsync(client, 7, "B");
        }
        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 8, "Продолжим");
            await SendEnglishAsync(client, 9, "пропустим");
            await SendEnglishAsync(client, 10, "К упражнению");
            await SendEnglishAsync(client, 11, "объясни could");
            await SendEnglishAsync(client, 12, "/answer Kim asks for the updated agenda. Yes, I can send it on Thursday.");
        }
        await using var db = Open();
        var diagnostic = (await db.Set<LearningProgram>().SingleAsync()).EnglishDiagnostic!;
        Assert.Equal(EnglishDiagnosticStatus.Skipped, diagnostic.Status);
        Assert.Equal(AnswerAssistance.Assisted, Assert.Single(diagnostic.Results).Assistance);
        var evidence = await db.Set<LearningEvidence>().SingleAsync();
        Assert.Equal(AnswerAssistance.Assisted, evidence.Assistance);
        Assert.Equal(0, LearnerSkillState.Evaluate([evidence]).PositiveAssessments);
        Assert.Equal(0, (await db.Set<ReviewSchedule>().SingleAsync()).SuccessfulReviews);
        Assert.Single(await db.Set<Lesson>().ToListAsync());
        Assert.Equal(1, probe.KindCalls.GetValueOrDefault("assess"));
        Assert.Empty(await db.InboxItems.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261112210000_AddFirstGoalConsultation"));
    }

    [Fact]
    public async Task Post_WritingFailureAndDelayedAnswer_PreservesReadingAndDoesNotAssessWrongItem()
    {
        var probe = new Probe("") { FailureKind = "assess" };
        using (var host = EnglishHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
            await SendEnglishAsync(client, 2, "Рабочие письма");
            await SendEnglishAsync(client, 3, "Начинаю с нуля");
            await SendEnglishAsync(client, 4, "начнём диагностику");
            await SendEnglishAsync(client, 5, "B");
            await SendEnglishAsync(client, 6, "ответ: I can send it today.");
        }
        await using (var db = Open())
        {
            var program = await db.Set<LearningProgram>().Include(value => value.Routes).SingleAsync();
            Assert.Single(program.EnglishDiagnostic!.Results);
            Assert.True(program.DiagnosticPending);
            Assert.Single(program.Routes);
            Assert.Empty(await db.Set<Lesson>().ToListAsync());
        }
        var retry = new Probe("");
        using (var host = DialogueHost(retry))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 7, "Продолжим");
            await SendEnglishAsync(client, 8, "ответ: I can send it tomorrow.");
            using var duplicate = await PostPublicAsync(client, 8, "ответ: I can send it tomorrow.");
            duplicate.EnsureSuccessStatusCode();
        }
        await using var result = Open();
        Assert.Equal(2, (await result.Set<LearningProgram>().SingleAsync()).EnglishDiagnostic!.Results.Length);
        Assert.Single(await result.Set<Lesson>().ToListAsync());
        Assert.Empty(await result.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(1, retry.KindCalls.GetValueOrDefault("assess"));
    }

    private WebApplicationFactory<Program> EnglishHost(Probe probe) => ConsultationHost(probe).WithWebHostBuilder(builder =>
        builder.UseSetting("Learning:Processing:EnglishDiagnosticEnabled", "true"));

    [Fact]
    public async Task Post_AnswersQueuedForSamePrompt_DoesNotApplySecondTextToNextDiagnosticItem()
    {
        var probe = new Probe("");
        using (var host = EnglishHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
            await SendEnglishAsync(client, 2, "Рабочие письма");
            await SendEnglishAsync(client, 3, "Начинаю с нуля");
            await SendEnglishAsync(client, 4, "начнём диагностику");
        }
        using (var paused = Host(probe, worker: false, publicIngress: true, configure: options =>
            { options.Enabled = true; options.DialogueEnabled = true; }))
        using (var client = paused.CreateClient())
        {
            using var reading = await PostPublicAsync(client, 5, "B");
            reading.EnsureSuccessStatusCode();
            using var early = await PostPublicAsync(client, 6, "ответ: I can send the list today.");
            early.EnsureSuccessStatusCode();
        }
        using var restarted = DialogueHost(probe);
        using var restartClient = restarted.CreateClient();
        await WaitAllPublicAsync(6);
        await using var db = Open();
        var program = await db.Set<LearningProgram>().SingleAsync();
        Assert.Single(program.EnglishDiagnostic!.Results);
        Assert.True(program.DiagnosticPending);
        Assert.Empty(await db.Set<Lesson>().ToListAsync());
        Assert.Equal(0, probe.Calls);
        Assert.Equal("diagnostic_unbound", (await db.Set<LearningRequest>().SingleAsync(request => request.SourceMessageId == 6)).Outcome);
    }

    [Fact]
    public async Task Process_FinalDiagnosticApplyFails_RecoversSavedAssessmentWithoutDuplicatingLessonOrRoute()
    {
        var probe = new Probe("");
        using (var host = EnglishHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
            await SendEnglishAsync(client, 2, "Рабочие письма");
            await SendEnglishAsync(client, 3, "Начинаю с нуля");
            await SendEnglishAsync(client, 4, "начнём диагностику");
            await SendEnglishAsync(client, 5, "B");
        }
        using (var faulted = Host(probe, worker: false, publicIngress: true, interceptor: new RejectBoundary("apply"),
            configure: options => { options.Enabled = true; options.DialogueEnabled = true; }))
        using (var client = faulted.CreateClient())
        {
            using var receipt = await PostPublicAsync(client, 6, "ответ: Yes, I can send it today.");
            receipt.EnsureSuccessStatusCode();
            using var scope = faulted.Services.CreateScope();
            var lease = (await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        }
        await using (var db = Open())
        {
            var program = await db.Set<LearningProgram>().Include(value => value.Routes).SingleAsync();
            Assert.Single(program.EnglishDiagnostic!.Results);
            Assert.Single(program.Routes);
            Assert.Empty(await db.Set<Lesson>().ToListAsync());
            Assert.Equal(5, await db.Set<LearningAppliedOperation>().CountAsync());
            Assert.NotNull((await db.Set<LearningProviderStep>().SingleAsync()).Result);
            await db.Database.ExecuteSqlRawAsync("UPDATE learning_requests SET lease_until = clock_timestamp() - interval '1 second' WHERE status = 2");
        }
        using var restarted = DialogueHost(probe);
        using var restartClient = restarted.CreateClient();
        await WaitAllPublicAsync(6);
        await using var result = Open();
        var completed = await result.Set<LearningProgram>().Include(value => value.Routes).SingleAsync();
        Assert.Equal(2, completed.EnglishDiagnostic!.Results.Length);
        Assert.Equal(2, completed.Routes.Count);
        Assert.Single(await result.Set<Lesson>().ToListAsync());
        Assert.Equal(6, await result.Set<LearningAppliedOperation>().CountAsync());
        Assert.Equal(1, probe.KindCalls.GetValueOrDefault("assess"));
    }

    [Fact]
    public async Task Post_ProgramConfirmationDuringWriting_ResolvesConsentWithoutAssessingConfirmationText()
    {
        var probe = new Probe("");
        using var host = EnglishHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
        await SendEnglishAsync(client, 2, "Рабочие письма");
        await SendEnglishAsync(client, 3, "Начинаю с нуля");
        await SendEnglishAsync(client, 4, "начнём диагностику");
        await SendEnglishAsync(client, 5, "B");
        var other = LearningProgram.Create(_user, "Algebra", "Beginner", null, "Mathematics", [new("root", "Fractions", [])], null);
        await using (var db = Open())
        {
            db.Add(other);
            await db.SaveChangesAsync();
        }
        await SendEnglishAsync(client, 6, "/learn_select " + other.Id);
        string code;
        await using (var db = Open()) code = (await db.Set<DialogueContext>().SingleAsync()).PendingConfirmation!.Code;
        await SendEnglishAsync(client, 7, "подтверждаю " + code);
        await using var result = Open();
        Assert.Equal(other.Id, (await result.Set<LearningContext>().SingleAsync()).ProgramId);
        Assert.Empty(await result.Set<Lesson>().ToListAsync());
        var diagnostic = (await result.Set<LearningProgram>().SingleAsync(program => program.Id != other.Id)).EnglishDiagnostic!;
        Assert.Single(diagnostic.Results);
        Assert.Equal(0, probe.KindCalls.GetValueOrDefault("assess"));
        Assert.Equal(0, probe.KindCalls.GetValueOrDefault("intent"));
        Assert.Null((await result.Set<DialogueContext>().SingleAsync()).PendingConfirmation);
    }

    [Theory]
    [InlineData("ответ")]
    [InlineData("вопрос")]
    [InlineData("не понял")]
    public async Task Post_WritingControlOrHelp_DoesNotAssessControlWords(string control)
    {
        var probe = new Probe("");
        using var host = EnglishHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "Хочу читать и писать на английском");
        await SendEnglishAsync(client, 2, "Рабочие письма");
        await SendEnglishAsync(client, 3, "Начинаю с нуля");
        await SendEnglishAsync(client, 4, "начнём диагностику");
        await SendEnglishAsync(client, 5, "B");
        await SendEnglishAsync(client, 6, control);
        await using var db = Open();
        Assert.Single((await db.Set<LearningProgram>().SingleAsync()).EnglishDiagnostic!.Results);
        Assert.Empty(await db.Set<Lesson>().ToListAsync());
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Migrate_LegacySuccessfulReviewAndCompletedDiagnostic_PreservesHistoryWithoutInventingIndependence()
    {
        await SeedAsync("learn_review");
        await using var db = Open();
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE learning_lessons SET "Assistance" = 0;
            UPDATE learning_evidence SET "Assistance" = 0;
            UPDATE learning_reviews SET "SuccessfulReviews" = 2;
            UPDATE learning_programs SET "DiagnosticQuestion" = 'Legacy question', "DiagnosticAnswer" = 'Legacy answer', "DiagnosticPending" = false;
            """);
        var due = (await db.Set<ReviewSchedule>().AsNoTracking().SingleAsync()).DueAt;
        await db.GetService<IMigrator>().MigrateAsync("20261112210000_AddFirstGoalConsultation");
        await db.Database.MigrateAsync();
        var program = await db.Set<LearningProgram>().Include(value => value.Routes).SingleAsync();
        Assert.Null(program.EnglishDiagnostic);
        Assert.False(program.DiagnosticPending);
        Assert.Equal("Legacy answer", program.DiagnosticAnswer);
        Assert.Single(program.Routes);
        var evidence = await db.Set<LearningEvidence>().SingleAsync();
        Assert.Equal(AnswerAssistance.Unknown, evidence.Assistance);
        Assert.True(evidence.AssessedCorrect);
        Assert.Equal(SkillMastery.Unknown, LearnerSkillState.Evaluate([evidence]).Mastery);
        var schedule = await db.Set<ReviewSchedule>().SingleAsync();
        Assert.Equal(due, schedule.DueAt);
        Assert.Equal(0, schedule.SuccessfulReviews);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private async Task SendEnglishAsync(HttpClient client, int id, string text)
    {
        using var response = await PostPublicAsync(client, id, text);
        response.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(id);
    }
}
