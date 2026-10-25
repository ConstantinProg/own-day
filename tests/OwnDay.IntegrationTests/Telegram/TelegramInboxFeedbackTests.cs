using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Delivery;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class TelegramInboxFeedbackTests
{
    private static readonly UserId Owner = new(101);

    [Theory]
    [InlineData(InboxTargetKind.Action, "Task")]
    [InlineData(InboxTargetKind.Project, "Project")]
    [InlineData(InboxTargetKind.SomedayMaybe, "Idea")]
    [InlineData(InboxTargetKind.Reference, "Note")]
    [InlineData(InboxTargetKind.WaitingFor, "Waiting")]
    public async Task ApplyAsync_EachKind_QueuesOneReadableNotificationWithoutConfirmation(InboxTargetKind kind, string label)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var id = await SeedAsync(factory, client, [Proposal(kind, "Canonical title")]);
        await ApplyAsync(factory, id);
        await ApplyAsync(factory, id);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var feedback = await db.TelegramOutboxMessages.SingleAsync(m => m.Id == id);
        Assert.Equal(101, feedback.ChatId);
        Assert.Contains(label, feedback.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Canonical title", feedback.Text, StringComparison.Ordinal);
        Assert.Contains("/inbox_result 1", feedback.Text, StringComparison.Ordinal);
        var keyboard = JsonSerializer.Deserialize<InlineKeyboardMarkup>(feedback.ReplyMarkupJson!)!;
        Assert.Equal("iu:1", Assert.Single(Assert.Single(keyboard.InlineKeyboard)).CallbackData);
        Assert.Contains("/inbox_undo 1", feedback.Text, StringComparison.Ordinal);
        Assert.Equal(2, await db.TelegramOutboxMessages.CountAsync()); // Capture reply plus one result.
        Assert.Single((await db.InboxItems.SingleAsync()).Targets);
        await PostTextAsync(client, 2, "/inbox_result 1");
        Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), m => m.Text.Contains("Original entry #1: original", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(20)]
    public async Task ApplyAsync_CompositeAndMaximumLength_ListsEveryTargetInOneBoundedMessage(int count)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var proposals = Enumerable.Range(1, count).Select(i => Proposal((InboxTargetKind)(i % 5), $"Entry {i} " + new string('x', 180))).ToArray();
        var id = await SeedAsync(factory, client, proposals);
        await ApplyAsync(factory, id);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var feedback = await db.TelegramOutboxMessages.SingleAsync(m => m.Id == id);
        Assert.InRange(feedback.Text.Length, 1, 4096);
        Assert.Equal(2, await db.TelegramOutboxMessages.CountAsync());
        foreach (var i in Enumerable.Range(1, count))
        {
            Assert.Contains($"Entry {i} ", feedback.Text, StringComparison.Ordinal);
        }
        Assert.Equal(count, (await db.InboxItems.SingleAsync()).Targets.Count);
        await PostTextAsync(client, 2, "/inbox_result 1");
        Assert.All(await db.TelegramOutboxMessages.ToListAsync(), m => Assert.InRange(m.Text.Length, 1, 4096));
    }

    [Theory]
    [InlineData(InboxAnalysisStatus.Failed, false)]
    [InlineData(InboxAnalysisStatus.InvalidResponse, false)]
    [InlineData(InboxAnalysisStatus.Completed, true)]
    public async Task ApplyAsync_AnalysisFailureOrPolicyRejection_PreservesInboxAndOffersManualAction(InboxAnalysisStatus status, bool invalid)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var id = await SeedAsync(factory, client, [Proposal(InboxTargetKind.Action, invalid ? " " : "Action")], status);
        await ApplyAsync(factory, id);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var feedback = await db.TelegramOutboxMessages.SingleAsync(m => m.Id == id);
        Assert.Contains("saved", feedback.Text, StringComparison.Ordinal);
        Assert.Contains("/inbox 1", feedback.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-provider-error", feedback.Text, StringComparison.Ordinal);
        Assert.Equal(InboxItemStatus.Active, (await db.InboxItems.SingleAsync()).Status);
        Assert.Empty(await db.Actions.ToListAsync());
        // The existing manual workflow is still usable after a failed automatic assessment.
        await PostTextAsync(client, 2, "/inbox 1");
        await PostTextAsync(client, 3, "1");
        await PostTextAsync(client, 4, ".");
        await PostTextAsync(client, 5, "0");
        await PostTextAsync(client, 6, "yes");
        Assert.Equal("original", (await db.Actions.SingleAsync()).Title);
        await PostTextAsync(client, 7, "/inbox_result 1");
        Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), m => m.Text.Contains("processed", StringComparison.Ordinal) &&
            m.Text.Contains("Original entry #1", StringComparison.Ordinal) && !m.Text.Contains("could not", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Callback_ConfirmOrRejectAfterScopeRestart_ResolvesOnceAndDeduplicatesUpdates(bool accept)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var id = await SeedAsync(factory, client, [Proposal(InboxTargetKind.Action, "Proposed action")]);
        await ApplyAsync(factory, id, InboxPolicyEngine.LegacyVersion);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            var prompt = await db.TelegramOutboxMessages.SingleAsync(m => m.Id == id);
            var keyboard = JsonSerializer.Deserialize<InlineKeyboardMarkup>(prompt.ReplyMarkupJson!)!;
            var buttons = Assert.Single(keyboard.InlineKeyboard).ToArray();
            Assert.Equal(2, buttons.Length);
            Assert.All(buttons, b => Assert.InRange(Encoding.UTF8.GetByteCount(b.CallbackData!), 1, 64));
            Assert.Equal($"ia:y:{id:N}", buttons[0].CallbackData);
            Assert.Equal($"ia:n:{id:N}", buttons[1].CallbackData);
            Assert.Empty(await db.Actions.ToListAsync());
        }
        await PostCallbackAsync(client, 2, id, accept);
        await PostCallbackAsync(client, 2, id, accept); // Exact Telegram redelivery.
        await PostCallbackAsync(client, 3, id, !accept); // Old opposite button.
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var application = await saved.InboxAutomaticApplications.SingleAsync();
        Assert.Equal(accept ? InboxConfirmationOutcome.Applied : InboxConfirmationOutcome.Rejected, application.ConfirmationOutcome);
        Assert.NotNull(application.ResolvedAt);
        Assert.Equal(accept ? 1 : 0, await saved.Actions.CountAsync());
        Assert.Equal(accept ? InboxItemStatus.Processed : InboxItemStatus.Active, (await saved.InboxItems.SingleAsync()).Status);
        Assert.Equal(4, await saved.TelegramOutboxMessages.CountAsync()); // Capture, preview, resolution, old-button explanation.
        Assert.Equal(3, await saved.ProcessedTelegramUpdates.CountAsync());
        Assert.Contains("Proposed action", application.ProposalsJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("reject")]
    [InlineData("expired")]
    public async Task ConfirmationCommand_WithoutTelegramDestination_QueuesResolutionReplyOnce(string scenario)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var id = await SeedAsync(factory, client, [Proposal(InboxTargetKind.Action, "Proposed action")]);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            await db.TelegramInboxDestinations.ExecuteDeleteAsync();
        }
        await ApplyAsync(factory, id, InboxPolicyEngine.LegacyVersion);
        if (scenario == "expired")
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            await db.InboxAutomaticApplications.ExecuteUpdateAsync(s => s.SetProperty(a => a.EvaluatedAt, DateTime.UtcNow.AddDays(-2)));
        }

        var command = $"/inbox_{(scenario == "reject" ? "reject" : "accept")} {id:N}";
        await PostTextAsync(client, 2, command);
        await PostTextAsync(client, 2, command);

        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var replies = await saved.TelegramOutboxMessages.OrderBy(m => m.CreatedAt).ToListAsync();
        Assert.Equal(2, replies.Count);
        var reply = replies[1];
        Assert.Equal(Owner.Value, reply.ChatId);
        Assert.Null(reply.ReplyMarkupJson);
        var outcome = scenario switch
        {
            "accept" => InboxConfirmationOutcome.Applied,
            "reject" => InboxConfirmationOutcome.Rejected,
            _ => InboxConfirmationOutcome.Stale
        };
        Assert.Equal(outcome, (await saved.InboxAutomaticApplications.SingleAsync()).ConfirmationOutcome);
        Assert.Equal(scenario == "accept" ? 1 : 0, await saved.Actions.CountAsync());
        Assert.Equal(scenario == "accept" ? InboxItemStatus.Processed : InboxItemStatus.Active,
            (await saved.InboxItems.SingleAsync()).Status);
        Assert.Contains(scenario switch
        {
            "accept" => "Proposed action",
            "reject" => "rejected",
            _ => "out of date"
        }, reply.Text, StringComparison.Ordinal);
        Assert.Contains(scenario == "accept" ? "/inbox_result 1" : "/inbox 1", reply.Text, StringComparison.Ordinal);
        Assert.Equal(2, await saved.TelegramOutboxMessages.CountAsync());
        Assert.Equal(2, await saved.ProcessedTelegramUpdates.CountAsync());
        Assert.Empty(await saved.TelegramInboxDestinations.ToListAsync());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("superseded")]
    [InlineData("manual")]
    [InlineData("discarded")]
    [InlineData("input")]
    public async Task Callback_StaleProposal_DoesNotApplyAndExplainsCurrentState(string scenario)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var id = await SeedAsync(factory, client, [Proposal(InboxTargetKind.Action, "Old")]);
        await ApplyAsync(factory, id, InboxPolicyEngine.LegacyVersion);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            var item = await db.InboxItems.SingleAsync();
            if (scenario == "expired")
            {
                await db.InboxAutomaticApplications.ExecuteUpdateAsync(s => s.SetProperty(a => a.EvaluatedAt, DateTime.UtcNow.AddDays(-2)));
            }
            if (scenario == "superseded")
            {
                var old = await db.InboxAnalyses.SingleAsync();
                db.InboxAnalyses.Add(InboxAnalysis.Start(Owner, item.Id, 2, new("fake", "inbox-v1", "test"),
                    old.ContextJson, old.ContextFingerprint, old.InputFingerprint, DateTime.UtcNow));
            }
            if (scenario == "manual")
            {
                await scope.ServiceProvider.GetRequiredService<InboxProcessingService>()
                    .ProcessAsync(Owner, new(item.Id, InboxTargetKind.Reference, "Manual"), CancellationToken.None);
            }
            if (scenario == "discarded")
            {
                item.Discard(DateTime.UtcNow);
            }
            if (scenario == "input")
            {
                db.Entry(item).Property(i => i.OriginalText).CurrentValue = "Changed";
            }
            await db.SaveChangesAsync();
        }
        await PostCallbackAsync(client, 2, id, true);
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await saved.Actions.ToListAsync());
        Assert.Equal(InboxConfirmationOutcome.Stale, (await saved.InboxAutomaticApplications.SingleAsync()).ConfirmationOutcome);
        Assert.Contains(await saved.TelegramOutboxMessages.ToListAsync(), m => m.Text.Contains("out of date", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callback_ForeignOwner_DoesNotRevealOrApplyProposal()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var id = await SeedAsync(factory, client, [Proposal(InboxTargetKind.Action, "Private")]);
        await ApplyAsync(factory, id, InboxPolicyEngine.LegacyVersion);
        await PostCallbackAsync(client, 2, id, true, 202);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.Actions.ToListAsync());
        Assert.Null((await db.InboxAutomaticApplications.SingleAsync()).ConfirmationOutcome);
        Assert.DoesNotContain("Private", (await db.TelegramOutboxMessages.SingleAsync(m => m.ChatId == 202)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeliverBatchAsync_FailureAndNewScope_RetriesSameIntentWithoutCreatingTargets()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var id = await SeedAsync(factory, client, [Proposal(InboxTargetKind.Action, "Once")]);
        await ApplyAsync(factory, id, InboxPolicyEngine.LegacyVersion);
        var sender = new RetryingSender();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            await Delivery(db, sender).DeliverBatchAsync(CancellationToken.None);
            Assert.Equal(1, (await db.TelegramOutboxMessages.SingleAsync(m => m.Id == id)).AttemptCount);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            await db.TelegramOutboxMessages.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));
            await Delivery(db, sender).DeliverBatchAsync(CancellationToken.None);
            Assert.Equal(OutboxMessageStatus.Sent, (await db.TelegramOutboxMessages.SingleAsync(m => m.Id == id)).Status);
            Assert.Empty(await db.Actions.ToListAsync());
        }
        await PostCallbackAsync(client, 2, id, true);
        await ApplyAsync(factory, id);
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Single(await saved.Actions.ToListAsync());
        Assert.Equal(3, await saved.TelegramOutboxMessages.CountAsync());
        Assert.Equal(2, sender.Markups.Count);
        Assert.Equal(sender.Markups[0], sender.Markups[1]);
    }

    private static TelegramOutboxDeliveryService Delivery(OwnDayDbContext db, ITelegramMessageSender sender) =>
        new(db, sender, NullLogger<TelegramOutboxDeliveryService>.Instance, TimeProvider.System);

    [Fact]
    public async Task ApplyAsync_SupersededFailure_DoesNotQueueMisleadingAttentionMessage()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var id = await SeedAsync(factory, client, [], InboxAnalysisStatus.Failed);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            var old = await db.InboxAnalyses.SingleAsync();
            db.InboxAnalyses.Add(InboxAnalysis.Start(Owner, 1, 2, new("fake", "inbox-v1", "test"),
                old.ContextJson, old.ContextFingerprint, old.InputFingerprint, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }
        await ApplyAsync(factory, id);
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Single(await saved.TelegramOutboxMessages.ToListAsync()); // Only capture acknowledgement.
        Assert.Equal(InboxItemStatus.Active, (await saved.InboxItems.SingleAsync()).Status);
    }

    [Fact]
    public async Task ApplyAsync_SelectedRussianLocale_UsesLocalizedAccessibleFeedbackAndRetainsUnicode()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var title = string.Concat(Enumerable.Repeat("😀", 95)) + " text";
        var id = await SeedAsync(factory, client, Enumerable.Range(1, 20).Select(_ => Proposal(InboxTargetKind.Reference, title)).ToArray());
        await PostTextAsync(client, 2, "/language");
        await PostTextAsync(client, 3, "2");
        await ApplyAsync(factory, id);
        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var feedback = await db.TelegramOutboxMessages.SingleAsync(m => m.Id == id);
        Assert.Contains("Входящие #1: создано.", feedback.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFFFD", feedback.Text, StringComparison.Ordinal);
        Assert.Equal(20, feedback.Text.Split('\n').Count(line => line.Contains("😀", StringComparison.Ordinal)));
        Assert.InRange(feedback.Text.Length, 1, 4096);
        foreach (var line in feedback.Text.Split('\n').Where(l => l.Contains('…')))
        {
            Assert.False(char.IsHighSurrogate(line[^2]));
        }
    }

    private sealed class RetryingSender : ITelegramMessageSender
    {
        public List<string> Markups { get; } = [];
        public Task SendMessageAsync(long chatId, string text, string? replyMarkupJson, CancellationToken cancellationToken)
        {
            if (replyMarkupJson is not null)
            {
                Markups.Add(replyMarkupJson);
                if (Markups.Count == 1)
                {
                    throw new HttpRequestException("Temporary delivery failure");
                }
            }
            return Task.CompletedTask;
        }
    }

    private static async Task ApplyAsync(OwnDayHostFactory factory, Guid id, string version = InboxPolicyEngine.Version)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<InboxAutomaticProcessingService>().ApplyAsync(Owner, 1, id, version, CancellationToken.None);
    }

    private static async Task<Guid> SeedAsync(OwnDayHostFactory factory, HttpClient client, InboxProposal[] proposals,
        InboxAnalysisStatus status = InboxAnalysisStatus.Completed)
    {
        await PostTextAsync(client, 1, "original");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var item = await db.InboxItems.SingleAsync();
        var json = JsonSerializer.Serialize(new InboxAnalysisContext(null, [], false), InboxAnalysisJson.Options);
        var now = DateTime.UtcNow;
        var analysis = InboxAnalysis.Start(Owner, item.Id, 1, new("fake", "inbox-v1", "test"), json, Hash(json), Hash(item.OriginalText), now);
        analysis.Finish(status, now, "{\"code\":\"private-provider-error\"}",
            status == InboxAnalysisStatus.Completed ? new() { Items = proposals } : null);
        db.InboxAnalyses.Add(analysis);
        await db.SaveChangesAsync();
        return analysis.Id;
    }

    private static InboxProposal Proposal(InboxTargetKind kind, string title) => new()
    {
        Kind = kind, Title = title, Description = null, ProjectIds = [], Ambiguities = [],
        Properties = new() { Source = null, WaitingSince = null, WaitingSinceEvidence = null, UnresolvedDateText = null }
    };

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static Task PostTextAsync(HttpClient client, int id, string text) => PostAsync(client, new
    {
        update_id = id, message = new
        {
            message_id = id, date = 1790244000,
            from = new { id = Owner.Value, is_bot = false, first_name = "Test" },
            chat = new { id = Owner.Value, type = "private" }, text
        }
    });

    private static Task PostCallbackAsync(HttpClient client, int id, Guid analysisId, bool accept, long user = 101) => PostAsync(client, new
    {
        update_id = id, callback_query = new
        {
            id = "callback-" + id, chat_instance = "test-chat", data = $"ia:{(accept ? "y" : "n")}:{analysisId:N}",
            from = new { id = user, is_bot = false, first_name = "Test" },
            message = new
            {
                message_id = 100, date = 1790244000,
                from = new { id = 555, is_bot = true, first_name = "OwnDay" },
                chat = new { id = user, type = "private" }, text = "Saved proposal"
            }
        }
    });

    private static async Task PostAsync(HttpClient client, object update)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        request.Content = new StringContent(JsonSerializer.Serialize(update), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
