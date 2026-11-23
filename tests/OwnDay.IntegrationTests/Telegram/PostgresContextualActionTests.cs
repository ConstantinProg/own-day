using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Actions;
using OwnDay.Infrastructure.Telegram.Delivery;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_ExplainAssessmentButtonForIndependentPractice_OpensThatLesson(bool choice)
    {
        var programId = await SeedChoiceProgramAsync();
        const string earlierQuestion = "What was the earlier program exercise?";
        await using (var seed = Open())
        {
            var program = await seed.Set<LearningProgram>().Include(program => program.Routes).SingleAsync();
            var skill = Skill.Create(_user, "Earlier skill", "Explain an earlier result");
            var lesson = Lesson.Start(_user, "Earlier topic", "Earlier theory", earlierQuestion);
            lesson.LinkToProgram(program, "sum");
            lesson.LinkToSkill(skill);
            lesson.Complete("Earlier answer", "Earlier feedback", true);
            seed.AddRange(skill, lesson, LearningEvidence.FromAnswer(lesson, _now.AddDays(-1), "test"));
            await seed.SaveChangesAsync();
        }
        var material = choice ? ChoiceMaterial(ChoiceFormat.SingleChoice) : new LessonMaterial("Practice theory", "What is the practice result?");
        using var host = ChoiceHost(new Probe("") { Material = material }, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn Practice");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, choice ? "А" : "/answer Practice answer");
        await DrainChoiceRequestsAsync(host.Services, 1);

        var button = await ActionButtonAsync(host.Services, LearningActionKind.ExplainAssessment);
        var codec = host.Services.GetRequiredService<TelegramActionCodec>();
        Assert.True(codec.TryDecode(button, _user.Value, _user.Value, out var action, out _));
        await PostChoiceCallbackAsync(client, 3, button);
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var saved = Open();
        var practice = await saved.Set<Lesson>().SingleAsync(lesson => lesson.PracticeProgramId == programId);
        Assert.Equal(LessonStatus.Completed, practice.Status);
        Assert.Equal(practice.Id, action!.TargetId);
        var requestId = await saved.Set<LearningRequest>().Where(request => request.SourceUpdateId == 3).Select(request => request.Id).SingleAsync();
        var replies = await saved.TelegramOutboxMessages.Where(message => message.LearningRequestId == requestId).Select(message => message.Text).ToListAsync();
        Assert.Contains(replies, text => text.Contains(material.Question, StringComparison.Ordinal));
        Assert.DoesNotContain(replies, text => text.Contains(earlierQuestion, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(LearningActionKind.Hint)]
    [InlineData(LearningActionKind.Solution)]
    public async Task Post_ExerciseHelpProviderFailure_OriginalButtonCanRetry(LearningActionKind kind)
    {
        var probe = new Probe("") { FailureKind = "explain", FailureCount = 2, Explanation = new("Recovered explanation") };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn Addition");
        await DrainChoiceRequestsAsync(host.Services, 1);
        var button = await ActionButtonAsync(host.Services, kind);
        long revision;
        await using (var before = Open())
            revision = (await before.Set<DialogueContext>().SingleAsync()).Revision;

        await PostChoiceCallbackAsync(client, 2, button);
        await DrainChoiceRequestsAsync(host.Services, 1);
        await using (var failed = Open())
        {
            Assert.Equal(2, probe.KindCalls["explain"]);
            Assert.Equal(revision, (await failed.Set<DialogueContext>().SingleAsync()).Revision);
            Assert.False(await failed.TelegramOutboxMessages.AnyAsync(message => message.EditMessageId != null));
            Assert.Contains(await failed.TelegramOutboxMessages.Select(message => message.Text).ToListAsync(),
                text => text.Contains("Повторите запрос", StringComparison.Ordinal));
        }

        await PostChoiceCallbackAsync(client, 3, button);
        await DrainChoiceRequestsAsync(host.Services, 1);
        await using var saved = Open();
        Assert.Equal(3, probe.KindCalls["explain"]);
        var requestId = await saved.Set<LearningRequest>().Where(request => request.SourceUpdateId == 3).Select(request => request.Id).SingleAsync();
        var replies = await saved.TelegramOutboxMessages.Where(message => message.LearningRequestId == requestId).ToListAsync();
        Assert.Contains(replies, message => message.Text == "Recovered explanation");
        Assert.Single(replies, message => message.EditMessageId == 1 && message.ReplyMarkupJson is null);
    }

    [Fact]
    public async Task Post_ConfirmationCallbackAcrossRestart_RacesWithTextAndAppliesOnce()
    {
        var probe = new Probe("") { Material = ChoiceMaterial(ChoiceFormat.SingleChoice) };
        string callback;
        string code;
        using (var first = ChoiceHost(probe, new()))
        using (var client = first.CreateClient())
        {
            await SendChoiceTextAsync(client, 1, "/learn Сложение");
            await DrainChoiceRequestsAsync(first.Services, 1);
            await SendChoiceTextAsync(client, 2, "/learn_finish");
            await DrainChoiceRequestsAsync(first.Services, 1);
            callback = await ActionButtonAsync(first.Services, LearningActionKind.Confirm);
            await using var db = Open();
            code = (await db.Set<DialogueContext>().SingleAsync()).PendingConfirmation!.Code;
            await PostChoiceCallbackAsync(client, 3, callback);
        }

        using var restarted = ChoiceHost(probe, new());
        using var resumed = restarted.CreateClient();
        await Task.WhenAll(PostChoiceCallbackAsync(resumed, 4, callback), SendChoiceTextAsync(resumed, 5, "/learn_confirm " + code));
        await DrainChoiceRequestsAsync(restarted.Services, 3);

        await using var saved = Open();
        Assert.Equal(1, await saved.Set<LearningRequest>().CountAsync(request => request.Outcome == "confirmation_resolved"));
        Assert.Null((await saved.Set<DialogueContext>().SingleAsync()).PendingConfirmation);
        Assert.Empty(await saved.Set<LearningEvidence>().ToListAsync());
        Assert.Single(await saved.TelegramOutboxMessages.Where(message => message.EditMessageId == 1 && message.ReplyMarkupJson == null).ToListAsync());
    }

    [Fact]
    public async Task Post_DeclineButtonForReplacedProposal_LeavesCurrentProposalAvailable()
    {
        var probe = new Probe("") { Material = ChoiceMaterial(ChoiceFormat.SingleChoice) };
        using var host = ChoiceHost(probe, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn Сложение");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, "/learn_finish");
        await DrainChoiceRequestsAsync(host.Services, 1);
        var old = await ActionButtonAsync(host.Services, LearningActionKind.Reject);
        await SendChoiceTextAsync(client, 3, "/cancel");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 4, "/learn_finish");
        await DrainChoiceRequestsAsync(host.Services, 1);
        var current = await ActionButtonAsync(host.Services, LearningActionKind.Reject);
        Assert.NotEqual(old, current);

        await PostChoiceCallbackAsync(client, 5, old);
        await DrainChoiceRequestsAsync(host.Services, 1);
        await using (var between = Open())
        {
            Assert.NotNull((await between.Set<DialogueContext>().SingleAsync()).PendingConfirmation);
            Assert.Equal("stale_target", (await between.Set<LearningRequest>().SingleAsync(request => request.SourceUpdateId == 5)).Outcome);
        }
        await PostChoiceCallbackAsync(client, 6, current);
        await DrainChoiceRequestsAsync(host.Services, 1);
        await using var saved = Open();
        Assert.Null((await saved.Set<DialogueContext>().SingleAsync()).PendingConfirmation);
        Assert.Equal(LessonStatus.AwaitingAnswer, (await saved.Set<Lesson>().SingleAsync()).Status);
        Assert.Equal("confirmation_declined", (await saved.Set<LearningRequest>().SingleAsync(request => request.SourceUpdateId == 6)).Outcome);
    }

    [Fact]
    public async Task Post_ExpiredSignedAction_IsAcknowledgedAndDoesNotAssess()
    {
        using var host = ChoiceHost(new Probe("") { Material = ChoiceMaterial(ChoiceFormat.SingleChoice) }, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn Сложение");
        await DrainChoiceRequestsAsync(host.Services, 1);

        var codec = host.Services.GetRequiredService<TelegramActionCodec>();
        var current = await ActionButtonAsync(host.Services, LearningActionKind.SelectChoice);
        Assert.True(codec.TryDecode(current, _user.Value, _user.Value, out var action, out _));
        var expired = codec.Encode(action!, _user.Value, _user.Value, DateTimeOffset.UtcNow.AddMinutes(-1));
        await PostChoiceCallbackAsync(client, 2, expired);
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var saved = Open();
        Assert.Empty((await saved.Set<Lesson>().SingleAsync()).Choice!.Attempts);
        Assert.Empty(await saved.Set<LearningEvidence>().ToListAsync());
        Assert.Equal("stale_target", (await saved.Set<LearningRequest>().SingleAsync(request => request.SourceUpdateId == 2)).Outcome);
    }

    [Fact]
    public async Task Process_PreviouslyAcceptedChoiceRequest_UsesSameAttemptAfterUpgrade()
    {
        using var host = ChoiceHost(new Probe("") { Material = ChoiceMaterial(ChoiceFormat.SingleChoice) }, new());
        using var client = host.CreateClient();
        await SendChoiceTextAsync(client, 1, "/learn Сложение");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await using (var seed = Open())
        {
            Assert.False(seed.Database.HasPendingModelChanges());
            var lesson = await seed.Set<Lesson>().SingleAsync();
            var choice = lesson.Choice!;
            var now = DateTime.UtcNow;
            seed.Add(new LearningRequest
            {
                UserId = _user, Sequence = 2, SourceKey = "telegram:update:2", SourceUpdateId = 2, SourceMessageId = 2, ChatId = _user.Value,
                PayloadKind = "learning_choice", PayloadVersion = 1, ReceivedAt = now, AvailableAt = now, Status = LearningRequestStatus.Ready,
                Outcome = null,
                NormalizedInput = JsonSerializer.Serialize(new ChoiceInput(lesson.Id, choice.Exercise.Version, choice.Revision, ChoiceAction.Select, 1),
                    JsonSerializerOptions.Web)
            });
            await seed.SaveChangesAsync();
        }

        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var saved = Open();
        Assert.Single((await saved.Set<Lesson>().SingleAsync()).Choice!.Attempts);
        Assert.Single(await saved.Set<LearningEvidence>().ToListAsync());
        Assert.Equal("choice_reply", (await saved.Set<LearningRequest>().SingleAsync(request => request.SourceUpdateId == 2)).Outcome);
    }

    [Fact]
    public async Task Post_PostponeInvitationCallback_ConsumesOnlyCurrentInvitation()
    {
        await SeedChoiceProgramAsync();
        await using (var seed = Open())
        {
            seed.Add(StudySchedule.Create(_user, "UTC", 1, [new(TimeOnly.MinValue, new TimeOnly(23, 59))],
                DateTimeOffset.UtcNow.AddDays(-2), StudyReminderType.Study));
            await seed.SaveChangesAsync();
        }
        using var host = ChoiceHost(new Probe(""), new()).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(TimeProvider.System))));
        using var client = host.CreateClient();
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<StudyInvitationDispatcher>().DispatchBatchAsync(default);
        var button = await ActionButtonAsync(host.Services, LearningActionKind.PostponeInvitation);
        await PostChoiceCallbackAsync(client, 1, button);
        await PostChoiceCallbackAsync(client, 2, button);
        await DrainChoiceRequestsAsync(host.Services, 2);

        await using var saved = Open();
        Assert.Null((await saved.Set<StudySchedule>().SingleAsync()).InvitationId);
        Assert.Single(await saved.TelegramOutboxMessages.Where(message => message.Text.StartsWith("Приглашение отложено")).ToListAsync());
        Assert.Empty(await saved.Set<Lesson>().ToListAsync());
    }

    [Theory]
    [InlineData(LearningActionKind.PostponeInvitation)]
    [InlineData(LearningActionKind.SkipInvitation)]
    public async Task Post_InvitationDismissalAndReplay_KeepOpenConfirmation(LearningActionKind kind)
    {
        await SeedChoiceProgramAsync();
        await using (var seed = Open())
        {
            seed.Add(StudySchedule.Create(_user, "UTC", 1, [new(TimeOnly.MinValue, new TimeOnly(23, 59))],
                DateTimeOffset.UtcNow.AddDays(-2), StudyReminderType.Study));
            await seed.SaveChangesAsync();
        }
        using var host = ChoiceHost(new Probe("") { Material = ChoiceMaterial(ChoiceFormat.SingleChoice) }, new())
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(TimeProvider.System))));
        using var client = host.CreateClient();
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<StudyInvitationDispatcher>().DispatchBatchAsync(default);
        var invitation = await ActionButtonAsync(host.Services, kind);
        await SendChoiceTextAsync(client, 1, "/learn Сложение");
        await DrainChoiceRequestsAsync(host.Services, 1);
        await SendChoiceTextAsync(client, 2, "/learn_finish");
        await DrainChoiceRequestsAsync(host.Services, 1);
        DialogueConfirmation? proposal;
        await using (var before = Open())
            proposal = (await before.Set<DialogueContext>().SingleAsync()).PendingConfirmation;
        Assert.NotNull(proposal);

        await PostChoiceCallbackAsync(client, 3, invitation);
        await DrainChoiceRequestsAsync(host.Services, 1);
        await using (var afterDismissal = Open())
        {
            Assert.Equal(proposal, (await afterDismissal.Set<DialogueContext>().SingleAsync()).PendingConfirmation);
            Assert.Null((await afterDismissal.Set<StudySchedule>().SingleAsync()).InvitationId);
        }
        await PostChoiceCallbackAsync(client, 4, invitation);
        await DrainChoiceRequestsAsync(host.Services, 1);

        await using var saved = Open();
        Assert.Equal(proposal, (await saved.Set<DialogueContext>().SingleAsync()).PendingConfirmation);
        Assert.Equal("stale_target", (await saved.Set<LearningRequest>().SingleAsync(request => request.SourceUpdateId == 4)).Outcome);
        Assert.Single(await saved.TelegramOutboxMessages.Where(message => message.EditMessageId == 1).ToListAsync());
    }

    private async Task<string> ActionButtonAsync(IServiceProvider services, LearningActionKind kind)
    {
        var codec = services.GetRequiredService<TelegramActionCodec>();
        await using var db = Open();
        var markup = await db.TelegramOutboxMessages.Where(message => message.ChatId == _user.Value && message.ReplyMarkupJson != null)
            .OrderByDescending(message => message.DeliveryOrder).Select(message => message.ReplyMarkupJson!).ToListAsync();
        return markup.SelectMany(json => JsonSerializer.Deserialize<InlineKeyboardMarkup>(json)!.InlineKeyboard.SelectMany(row => row))
            .Select(button => button.CallbackData!).First(data => codec.TryDecode(data, _user.Value, _user.Value, out var action, out _) && action?.Kind == kind);
    }
}
