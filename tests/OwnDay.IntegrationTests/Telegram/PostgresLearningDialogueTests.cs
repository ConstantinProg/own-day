using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Learning;
using Xunit;
using OwnDay.Application.Dialogue;
using OwnDay.Domain.Dialogue;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Process_ConfirmationPublicationCommitFails_RollsBackContextAuditAndRepliesThenRecovers()
    {
        await SeedAsync("learn_ask");
        var probe = new Probe("");
        using (var host = Host(probe, worker: false, publicIngress: true, interceptor: new RejectBoundary("apply"),
            configure: options => { options.Enabled = true; options.DialogueEnabled = true; }))
        using (var client = host.CreateClient())
        {
            using var accepted = await PostPublicAsync(client, 1, "/learn_finish");
            accepted.EnsureSuccessStatusCode();
            using var scope = host.Services.CreateScope();
            var lease = (await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        }
        await using (var db = Open())
        {
            Assert.Empty(await db.Set<DialogueContext>().ToListAsync());
            Assert.Empty(await db.Set<LearningAppliedOperation>().ToListAsync());
            Assert.Empty(await db.TelegramOutboxMessages.ToListAsync());
            Assert.Null((await db.Set<LearningRequest>().SingleAsync()).DialogueOutcomeJson);
            Assert.Equal(LessonStatus.AwaitingAnswer, (await db.Set<Lesson>().SingleAsync()).Status);
            await db.Database.ExecuteSqlRawAsync("UPDATE learning_requests SET lease_until = clock_timestamp() - interval '1 second'");
        }
        using var restarted = DialogueHost(probe);
        using var restartClient = restarted.CreateClient();
        await WaitAllPublicAsync(1);
        await using var result = Open();
        var context = await result.Set<DialogueContext>().SingleAsync();
        var request = await result.Set<LearningRequest>().SingleAsync();
        Assert.NotNull(context.PendingConfirmation);
        Assert.Equal("confirmation_published", request.Outcome);
        using var audit = System.Text.Json.JsonDocument.Parse(request.DialogueOutcomeJson!);
        Assert.Equal(context.PendingConfirmation.ProposalId, audit.RootElement.GetProperty("confirmation").GetProperty("proposalId").GetGuid());
        Assert.Single(await result.Set<LearningAppliedOperation>().ToListAsync());
    }

    [Fact]
    public async Task Post_StartAnnouncesMode_EnrolledUserKeepsDialogueWhileOtherUserKeepsInbox()
    {
        var probe = new Probe("");
        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient())
        {
            using var start = await PostPublicAsync(client, 1, "/start");
            start.EnsureSuccessStatusCode();
            await using var db = Open();
            Assert.Single(await db.Set<DialogueContext>().ToListAsync());
            Assert.Empty(await db.Set<LearningProgram>().ToListAsync());
            Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), message => message.Text.Contains("/capture", StringComparison.Ordinal));
        }
        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient())
        {
            using var input = await PostPublicAsync(client, 2, "Продолжим");
            input.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(1);
            await TelegramCurriculumTests.PostAsync(client, 3, 202, "A separate Inbox note");
        }
        await using var result = Open();
        Assert.Equal(202, (await result.InboxItems.SingleAsync()).UserId.Value);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Migrate_LegacyLearningState_BootstrapsDialogueWithoutInventingProgramOrReplayingRequest()
    {
        await SeedAsync("learn_ask");
        Guid id;
        await using (var db = Open())
        {
            id = (await db.Set<Lesson>().SingleAsync()).Id;
            // This fixture models a legacy lesson with no recorded independence.
            await db.Database.ExecuteSqlRawAsync("UPDATE learning_lessons SET \"Assistance\" = 0");
            await db.GetService<IMigrator>().MigrateAsync("20261112190000_AddSharedLearningLedger");
            var legacyInput = "{\"name\":\"learn_pause\",\"arguments\":\"\"}";
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO learning_requests (id, user_id, sequence, source_key, source_update_id, chat_id,
                    payload_kind, payload_version, normalized_input, status, outcome, received_at, completed_at, available_at, attempt_count)
                VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 101, 1, 'telegram:update:50', 50, 101,
                    'learning_command', 1, CAST({legacyInput} AS jsonb), 0, 'reply_committed', now(), now(), now(), 0);
                """);
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Set<DialogueContext>().ToListAsync());
            Assert.Equal(1, await db.Set<LearningProgram>().CountAsync());
        }
        var probe = new Probe("");
        using var host = DialogueHost(probe);
        using var client = host.CreateClient();
        using var recap = await PostPublicAsync(client, 1, "Продолжим");
        recap.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(2);
        await using var result = Open();
        Assert.Equal(id, (await result.Set<Lesson>().SingleAsync()).Id);
        Assert.Equal(1, await result.Set<LearningProgram>().CountAsync());
        Assert.Equal(id, (await result.Set<DialogueContext>().SingleAsync()).TargetLessonId);
        Assert.Equal(0, probe.Calls);
        Assert.Equal(1, await result.Set<LearningAppliedOperation>().CountAsync());
    }

    [Fact]
    public async Task Post_LateMessageOlderThanQuestion_DoesNotRebindToNewLesson()
    {
        await SeedAsync("learn_request");
        var probe = new Probe("");
        using var host = DialogueHost(probe);
        using var client = host.CreateClient();
        using var next = await PostPublicAsync(client, 10, "/learn_next");
        next.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(1);
        using var late = await PostPublicAsync(client, 11, "My old answer", messageId: 9);
        late.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(2);
        Assert.False(probe.KindCalls.ContainsKey("assess"));
        Assert.False(probe.KindCalls.ContainsKey("intent"));
        using var current = await PostPublicAsync(client, 12, "A fresh answer", messageId: 12);
        current.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(3);
        Assert.Equal("A fresh answer", Assert.Single(probe.AssessedAnswers));
        await using var result = Open();
        Assert.Single(await result.Set<LearningEvidence>().Where(e => e.ObservedAt == _now).ToListAsync());
    }

    [Fact]
    public async Task Post_InterruptedDuringBlockedClassifier_ReleasesUserLockAndDoesNotAssessLateResult()
    {
        await SeedAsync("learn_ask");
        var probe = new Probe("intent");
        using var host = DialogueHost(probe);
        using var client = host.CreateClient();
        using var input = await PostPublicAsync(client, 1, "My answer to the question");
        input.EnsureSuccessStatusCode();
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await using (var db = Open())
            {
                db.Database.SetCommandTimeout(3);
                await using var transaction = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({_user.Value})");
                Assert.Equal(LearningRequestStatus.Running, (await db.Set<LearningRequest>().SingleAsync()).Status);
            }
            using var capture = await PostPublicAsync(client, 2, "/capture Keep this note");
            capture.EnsureSuccessStatusCode();
        }
        finally { probe.Release.TrySetResult(); }
        await WaitAllPublicAsync(1);
        await using var result = Open();
        Assert.Equal("stale_target", (await result.Set<LearningRequest>().SingleAsync()).Outcome);
        Assert.Equal(DialogueExpectedResponse.None, (await result.Set<DialogueContext>().SingleAsync()).ExpectedResponse);
        Assert.Equal("Keep this note", (await result.InboxItems.SingleAsync()).OriginalText);
        Assert.False(probe.KindCalls.ContainsKey("assess"));
        Assert.True((await result.OpenAIRequestAttempts.SingleAsync()).ChargedUsd > 0);
    }

    [Fact]
    public async Task Post_UnsupportedContextSchema_RejectsBeforeReceiptWithoutChangingState()
    {
        await SeedAsync("learn_ask");
        var probe = new Probe("");
        using var host = DialogueHost(probe);
        using var client = host.CreateClient();
        using var recap = await PostPublicAsync(client, 1, "Продолжим");
        recap.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(1);
        await using (var db = Open()) await db.Database.ExecuteSqlRawAsync("UPDATE dialogue_contexts SET schema_version = 99");
        using var rejected = await PostPublicAsync(client, 2, "My answer");
        Assert.False(rejected.IsSuccessStatusCode);
        await using var result = Open();
        Assert.Single(await result.Set<LearningRequest>().ToListAsync());
        Assert.Single(await result.ProcessedTelegramUpdates.ToListAsync());
        Assert.Equal(99, (await result.Set<DialogueContext>().SingleAsync()).SchemaVersion);
        Assert.Equal(LessonStatus.AwaitingAnswer, (await result.Set<Lesson>().SingleAsync()).Status);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_SelectProposalTargetVersionChanges_RejectsConsentAndKeepsSelectedProgram()
    {
        await SeedAsync("learn_request");
        Guid target;
        Guid selected;
        await using (var db = Open())
        {
            selected = (await db.Set<LearningContext>().SingleAsync()).ProgramId;
            var program = LearningProgram.Create(_user, "Another practical goal", "Beginner", null, "English", [new("root", "Read another email", [])], null);
            target = program.Id;
            db.Add(program);
            await db.SaveChangesAsync();
        }
        var probe = new Probe("");
        using var host = DialogueHost(probe);
        using var client = host.CreateClient();
        using var select = await PostPublicAsync(client, 1, "/learn_select " + target);
        select.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(1);
        string code;
        await using (var db = Open())
        {
            var proposal = (await db.Set<DialogueContext>().SingleAsync()).PendingConfirmation!;
            code = proposal.Code;
            Assert.Equal(target, proposal.Target!.ProgramId);
            Assert.True(proposal.Target.ProgramVersion > 0);
            Assert.Contains("Another practical goal", string.Join(" ", await db.TelegramOutboxMessages.Select(message => message.Text).ToArrayAsync()));
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE learning_programs SET \"Experience\" = \"Experience\" WHERE \"Id\" = {target}");
        }
        using var stale = await PostPublicAsync(client, 2, "/learn_confirm " + code);
        stale.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(2);
        await using var result = Open();
        Assert.Equal(selected, (await result.Set<LearningContext>().SingleAsync()).ProgramId);
        Assert.Null((await result.Set<DialogueContext>().SingleAsync()).PendingConfirmation);
        Assert.Equal("confirmation_rejected", (await result.Set<LearningRequest>().SingleAsync(request => request.SourceUpdateId == 2)).Outcome);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_ChoiceAcceptedBeforePromptPublication_DoesNotAssessUntilNewBoundChoice()
    {
        await SeedAsync("learn_ask");
        var probe = new Probe("") { Intent = LearningIntent.Ambiguous };
        using (var host = Host(probe, worker: false, publicIngress: true, configure: options =>
            { options.Enabled = true; options.DialogueEnabled = true; }))
        using (var client = host.CreateClient())
        {
            using var original = await PostPublicAsync(client, 1, "Could you help me?");
            original.EnsureSuccessStatusCode();
            using var premature = await PostPublicAsync(client, 2, "ответ");
            premature.EnsureSuccessStatusCode();
        }
        using var restarted = DialogueHost(probe);
        using var restartClient = restarted.CreateClient();
        await WaitAllPublicAsync(2);
        Assert.False(probe.KindCalls.ContainsKey("assess"));
        using var bound = await PostPublicAsync(restartClient, 3, "ответ");
        bound.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(3);
        Assert.Equal("Could you help me?", Assert.Single(probe.AssessedAnswers));
        await using var result = Open();
        Assert.Empty(await result.InboxItems.ToListAsync());
    }

    [Fact]
    public async Task Post_ExplicitInboxMode_InvalidatesPendingConfirmationAndPreservesCapturedNote()
    {
        await SeedAsync("learn_ask");
        var probe = new Probe("");
        using var host = DialogueHost(probe);
        using var client = host.CreateClient();
        using var finish = await PostPublicAsync(client, 1, "/learn_finish");
        finish.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(1);
        string code;
        await using (var db = Open()) code = (await db.Set<DialogueContext>().SingleAsync()).PendingConfirmation!.Code;
        using var capture = await PostPublicAsync(client, 2, "/capture Preserve this note");
        capture.EnsureSuccessStatusCode();
        using var stale = await PostPublicAsync(client, 3, "подтверждаю " + code);
        stale.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(2);
        await using var result = Open();
        Assert.Equal("Preserve this note", (await result.InboxItems.SingleAsync()).OriginalText);
        Assert.Equal(LessonStatus.AwaitingAnswer, (await result.Set<Lesson>().SingleAsync()).Status);
        Assert.Null((await result.Set<DialogueContext>().SingleAsync()).PendingConfirmation);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_EnglishQuestionAsAnswer_ClassifiesAndAssessesOnceWithoutLeakingAnswerKey()
    {
        await using (var db = Open())
        {
            db.Add(Lesson.Start(_user, "English", "Write politely.", "Write a polite request asking for help.", expectedAnswer: "SECRET_REFERENCE"));
            await db.SaveChangesAsync();
        }
        var probe = new Probe("") { Intent = LearningIntent.Answer };
        using var host = DialogueHost(probe);
        using var client = host.CreateClient();
        using var response = await PostPublicAsync(client, 1, "Could you help me?");
        response.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(1);
        using var duplicate = await PostPublicAsync(client, 1, "Could you help me?");
        duplicate.EnsureSuccessStatusCode();
        Assert.Equal(1, probe.KindCalls["intent"]);
        Assert.Equal(1, probe.KindCalls["assess"]);
        var snapshot = System.Text.Json.JsonSerializer.Serialize(Assert.Single(probe.IntentInputs));
        Assert.DoesNotContain("SECRET_REFERENCE", snapshot);
        Assert.DoesNotContain("ExpectedAnswer", snapshot);
        await using var result = Open();
        Assert.Single(await result.Set<LearningEvidence>().ToListAsync());
        Assert.Equal("Could you help me?", (await result.Set<Lesson>().SingleAsync()).Answer);
        Assert.Empty(await result.InboxItems.ToListAsync());
        Assert.Equal(2, await result.OpenAIRequestAttempts.CountAsync());
        Assert.Equal(DialogueExpectedResponse.None, (await result.Set<DialogueContext>().SingleAsync()).ExpectedResponse);
    }

    [Fact]
    public async Task Post_AmbiguousInputAcrossRestart_AssessesOriginalTextOnlyAfterExplicitChoice()
    {
        await SeedAsync("learn_ask");
        var firstProbe = new Probe("") { Intent = LearningIntent.Ambiguous };
        using (var host = DialogueHost(firstProbe))
        using (var client = host.CreateClient())
        {
            using var response = await PostPublicAsync(client, 1, "Could you help me?");
            response.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(1);
            await using var observer = Open();
            Assert.Equal(DialogueExpectedResponse.IntentChoice, (await observer.Set<DialogueContext>().SingleAsync()).ExpectedResponse);
            Assert.False(firstProbe.KindCalls.ContainsKey("assess"));
        }
        var probe = new Probe("");
        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient())
        {
            using var unrelated = await PostPublicAsync(client, 2, "Some other free text");
            unrelated.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(2);
            Assert.Equal(0, probe.Calls);
            using var choice = await PostPublicAsync(client, 3, "ответ");
            choice.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(3);
        }
        Assert.Equal("Could you help me?", Assert.Single(probe.AssessedAnswers));
        Assert.False(probe.KindCalls.ContainsKey("intent"));
        await using var result = Open();
        Assert.Single(await result.Set<LearningEvidence>().Where(e => e.ObservedAt == _now).ToListAsync());
        Assert.Empty(await result.InboxItems.ToListAsync());
    }

    [Fact]
    public async Task Post_FinishConfirmationAcrossRestart_RejectsBareAndStaleConsentAndFinishesOnce()
    {
        await SeedAsync("learn_ask");
        var probe = new Probe("");
        string oldCode;
        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient())
        {
            using var finish = await PostPublicAsync(client, 1, "/learn_finish");
            finish.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(1);
            await using (var db = Open())
            {
                Assert.Equal(LessonStatus.AwaitingAnswer, (await db.Set<Lesson>().SingleAsync()).Status);
                oldCode = (await db.Set<DialogueContext>().SingleAsync()).PendingConfirmation!.Code;
            }
            using var bare = await PostPublicAsync(client, 2, "да");
            bare.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(2);
            using var pause = await PostPublicAsync(client, 3, "/learn_pause");
            pause.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(3);
        }
        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient())
        {
            using var stale = await PostPublicAsync(client, 4, "подтверждаю " + oldCode);
            stale.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(4);
            await using (var db = Open()) Assert.Equal(LessonStatus.Paused, (await db.Set<Lesson>().SingleAsync()).Status);
            using var finish = await PostPublicAsync(client, 5, "/learn_finish");
            finish.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(5);
        }
        string current;
        await using (var db = Open()) current = (await db.Set<DialogueContext>().SingleAsync()).PendingConfirmation!.Code;
        Assert.NotEqual(oldCode, current);
        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient())
        {
            using var confirm = await PostPublicAsync(client, 6, "/learn_confirm " + current);
            confirm.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(6);
            using var repeated = await PostPublicAsync(client, 7, "подтверждаю " + current);
            repeated.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(7);
        }
        await using var result = Open();
        Assert.Equal(LessonStatus.Finished, (await result.Set<Lesson>().SingleAsync()).Status);
        var resolution = await result.Set<LearningRequest>().SingleAsync(request => request.SourceUpdateId == 6);
        Assert.Equal("confirmation_resolved", resolution.Outcome);
        using (var audit = System.Text.Json.JsonDocument.Parse(resolution.DialogueOutcomeJson!))
            Assert.Equal((int)DialogueOperationKind.Finish, audit.RootElement.GetProperty("confirmation").GetProperty("operation").GetInt32());
        Assert.Empty(await result.Set<LearningEvidence>().Where(e => e.ObservedAt == _now).ToListAsync());
        Assert.Empty(await result.InboxItems.ToListAsync());
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_AnswerQueuedAfterPause_DoesNotAssessAfterTheExpectedResponseChanges()
    {
        await SeedAsync("learn_ask");
        var probe = new Probe("");
        using (var host = Host(probe, worker: false, publicIngress: true, configure: options =>
            { options.Enabled = true; options.DialogueEnabled = true; }))
        using (var client = host.CreateClient())
        {
            using var pause = await PostPublicAsync(client, 1, "/learn_pause");
            pause.EnsureSuccessStatusCode();
            using var answer = await PostPublicAsync(client, 2, "/answer My answer");
            answer.EnsureSuccessStatusCode();
        }
        using var restarted = DialogueHost(probe);
        using var restartClient = restarted.CreateClient();
        await WaitAllPublicAsync(2);
        await using var result = Open();
        Assert.Equal(LessonStatus.Paused, (await result.Set<Lesson>().SingleAsync()).Status);
        Assert.Empty(await result.Set<LearningEvidence>().Where(e => e.ObservedAt == _now).ToListAsync());
        Assert.Equal(0, probe.Calls);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> DialogueHost(Probe probe) =>
        Host(probe, worker: true, publicIngress: true).WithWebHostBuilder(builder =>
            builder.UseSetting("Learning:Processing:DialogueEnabled", "true"));

    [Fact]
    public async Task Post_NaturalClarification_PreservesQuestionWithoutAssessmentOrInboxCapture()
    {
        await SeedAsync("learn_ask");
        var probe = new Probe("");
        using var host = Host(probe, worker: true, publicIngress: true).WithWebHostBuilder(builder =>
            builder.UseSetting("Learning:Processing:DialogueEnabled", "true"));
        using var client = host.CreateClient();
        using var response = await PostPublicAsync(client, 1, "Объясни условие");
        response.EnsureSuccessStatusCode();
        await using (var observer = Open())
        {
            Assert.Empty(await observer.InboxItems.ToListAsync());
            Assert.Equal("learning_text", (await observer.Set<LearningRequest>().SingleAsync()).PayloadKind);
        }
        await WaitAllPublicAsync(1);
        await using var result = Open();
        var lesson = await result.Set<Lesson>().Include(l => l.Interactions).SingleAsync();
        Assert.Equal("What is requested?", lesson.Question);
        Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
        Assert.Single(lesson.Interactions);
        Assert.Empty(await result.Set<LearningEvidence>().Where(e => e.ObservedAt == _now).ToListAsync());
        Assert.False(probe.KindCalls.ContainsKey("assess"));
        Assert.Equal(1, probe.KindCalls["explain"]);
    }

    [Fact]
    public async Task Post_NaturalResumeAfterHostRestart_RestoresSameLessonWithoutGeneration()
    {
        await SeedAsync("learn_ask");
        var probe = new Probe("");
        using (var first = DialogueHost(probe))
        using (var firstClient = first.CreateClient())
        {
            using var pause = await PostPublicAsync(firstClient, 1, "Пауза");
            pause.EnsureSuccessStatusCode();
            await WaitAllPublicAsync(1);
        }
        Guid id;
        await using (var db = Open())
        {
            var paused = await db.Set<Lesson>().SingleAsync();
            id = paused.Id;
            Assert.Equal(LessonStatus.Paused, paused.Status);
            Assert.Equal(DialogueExpectedResponse.None, (await db.Set<DialogueContext>().SingleAsync()).ExpectedResponse);
        }
        using var host = DialogueHost(probe);
        using var client = host.CreateClient();
        using var response = await PostPublicAsync(client, 2, "Продолжим");
        response.EnsureSuccessStatusCode();
        await using (var observer = Open()) Assert.Empty(await observer.InboxItems.ToListAsync());
        await WaitAllPublicAsync(2);
        await using var result = Open();
        var restored = await result.Set<Lesson>().SingleAsync();
        Assert.Equal(id, restored.Id);
        Assert.Equal(LessonStatus.AwaitingAnswer, restored.Status);
        Assert.Equal(0, probe.Calls);
    }
}
