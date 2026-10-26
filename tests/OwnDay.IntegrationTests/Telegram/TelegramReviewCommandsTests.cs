using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class TelegramInboxFeedbackTests
{
    [Theory]
    [InlineData(true, "en")]
    [InlineData(false, "en")]
    [InlineData(true, "ru")]
    [InlineData(false, "ru")]
    public async Task Post_ReviewTextAlternative_ResolvesThePrintedOfferOnce(bool accept, string locale)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var analysis = await SeedAsync(factory, client, [Proposal(InboxTargetKind.Action, "Proposed task")]);
        await PostTextAsync(client, 2, "/language " + locale);
        await ApplyAsync(factory, analysis, InboxPolicyEngine.LegacyVersion);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            var prompt = await db.TelegramOutboxMessages.SingleAsync(m => m.Id == analysis);
            Assert.Contains("/accept 1 1", prompt.Text, StringComparison.Ordinal);
            Assert.Contains("/reject 1 1", prompt.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(analysis.ToString("N"), prompt.Text, StringComparison.Ordinal);
        }
        var command = accept ? "/accept 1 1" : "/reject 1 1";
        await PostTextAsync(client, 3, command);
        await PostTextAsync(client, 3, command);
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(accept ? 1 : 0, await saved.Actions.CountAsync());
        Assert.Equal(accept ? InboxConfirmationOutcome.Applied : InboxConfirmationOutcome.Rejected,
            (await saved.InboxAutomaticApplications.SingleAsync()).ConfirmationOutcome);
        Assert.Equal(4, await saved.TelegramOutboxMessages.CountAsync()); // Capture, language, offer, resolution.
    }

    [Fact]
    public async Task Post_OldReviewTextCommand_DoesNotApplyNewerOffer()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var oldId = await SeedAsync(factory, client, [Proposal(InboxTargetKind.Action, "Old task")]);
        await ApplyAsync(factory, oldId, InboxPolicyEngine.LegacyVersion);
        Guid newId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            var old = await db.InboxAnalyses.SingleAsync();
            var current = InboxAnalysis.Start(Owner, 1, 2, new("fake", "inbox-v1", "test"), old.ContextJson,
                old.ContextFingerprint, old.InputFingerprint, DateTime.UtcNow);
            current.Finish(InboxAnalysisStatus.Completed, DateTime.UtcNow, "{}", new() { Items = [Proposal(InboxTargetKind.Action, "New task")] });
            db.InboxAnalyses.Add(current);
            await db.SaveChangesAsync();
            newId = current.Id;
        }
        await ApplyAsync(factory, newId, InboxPolicyEngine.LegacyVersion);
        await PostTextAsync(client, 2, "/accept 1 1");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            Assert.Empty(await db.Actions.ToListAsync());
            Assert.Equal(InboxConfirmationOutcome.Stale, (await db.InboxAutomaticApplications.SingleAsync(a => a.AnalysisId == oldId)).ConfirmationOutcome);
            Assert.Null((await db.InboxAutomaticApplications.SingleAsync(a => a.AnalysisId == newId)).ConfirmationOutcome);
        }
        await PostTextAsync(client, 3, "/accept 1 2");
        using var verify = factory.Services.CreateScope();
        Assert.Equal("New task", (await verify.ServiceProvider.GetRequiredService<OwnDayDbContext>().Actions.SingleAsync()).Title);
    }

    [Fact]
    public async Task Post_ForeignReviewTextCommand_DoesNotRevealOriginalOrApply()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var analysis = await SeedAsync(factory, client, [Proposal(InboxTargetKind.Action, "Private proposal")]);
        await ApplyAsync(factory, analysis, InboxPolicyEngine.LegacyVersion);
        await PostAsync(client, new
        {
            update_id = 2,
            message = new
            {
                message_id = 2, date = 1792929600,
                from = new { id = 202, is_bot = false, first_name = "Other" },
                chat = new { id = 202, type = "private" }, text = "/accept 1 1"
            }
        });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.Actions.ToListAsync());
        Assert.DoesNotContain("Private", (await db.TelegramOutboxMessages.SingleAsync(m => m.ChatId == 202)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_ResolvedOldButton_DoesNotInterruptNewLanguageSelection()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var analysis = await SeedAsync(factory, client, [Proposal(InboxTargetKind.Action, "Task")]);
        await ApplyAsync(factory, analysis, InboxPolicyEngine.LegacyVersion);
        await PostCallbackAsync(client, 2, analysis, false);
        await PostTextAsync(client, 3, "/language");
        await PostCallbackAsync(client, 4, analysis, true);
        await PostTextAsync(client, 5, "2");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal("ru", (await db.TelegramUserLanguages.SingleAsync()).Locale);
        Assert.Empty(await db.Actions.ToListAsync());
        Assert.Single(await db.InboxItems.ToListAsync());
        Assert.Equal(InboxConfirmationOutcome.Rejected, (await db.InboxAutomaticApplications.SingleAsync()).ConfirmationOutcome);
    }
}
