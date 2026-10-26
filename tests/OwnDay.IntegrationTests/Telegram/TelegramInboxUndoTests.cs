using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class TelegramInboxFeedbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_CommandOrCallback_DeduplicatesAndAllowsManualCorrection(bool callback)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var analysis = await SeedAsync(factory, client,
            [Proposal(InboxTargetKind.Action, "Wrong task"), Proposal(InboxTargetKind.Reference, "Wrong note")]);
        await ApplyAsync(factory, analysis);
        async Task UndoAsync(int id)
        {
            if (callback)
            {
                await PostUndoCallbackAsync(client, id);
            }
            else
            {
                await PostTextAsync(client, id, "/inbox_undo 1");
            }
        }
        await UndoAsync(2);
        await UndoAsync(2);
        await UndoAsync(3);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            Assert.Empty(await db.Actions.ToListAsync());
            Assert.Empty(await db.References.ToListAsync());
            var messages = await db.TelegramOutboxMessages.ToListAsync();
            var success = Assert.Single(messages, m => m.Text.Contains("Original restored", StringComparison.Ordinal));
            Assert.Contains("Records affected: 2", success.Text, StringComparison.Ordinal);
            Assert.Contains("Task: 1", success.Text, StringComparison.Ordinal);
            Assert.Contains("Note: 1", success.Text, StringComparison.Ordinal);
            Assert.Single(messages, m => m.Text.Contains("Already undone", StringComparison.Ordinal));
            Assert.Equal(4, messages.Count); // Capture, application, successful Undo, repeated Undo.
            Assert.Single(await db.InboxAnalyses.ToListAsync());
        }
        await PostTextAsync(client, 4, "/inbox 1");
        await PostTextAsync(client, 5, "4");
        await PostTextAsync(client, 6, "Corrected note");
        await PostTextAsync(client, 7, "0");
        await PostTextAsync(client, 8, "yes");
        await UndoAsync(9);
        await PostTextAsync(client, 10, "/inbox_result 1");
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal("Corrected note", (await saved.References.SingleAsync()).Text);
        Assert.Empty(await saved.Actions.ToListAsync());
        Assert.NotNull((await saved.InboxAutomaticApplications.SingleAsync()).UndoneAt);
        Assert.Single(await saved.InboxAnalyses.ToListAsync());
        Assert.Contains(await saved.TelegramOutboxMessages.ToListAsync(), m =>
            m.Text.Contains("processed", StringComparison.Ordinal) && m.Text.Contains("Original entry #1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Undo_ForeignCallbackThenCompletedTask_RefusesAndPreservesResult()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var analysis = await SeedAsync(factory, client, [Proposal(InboxTargetKind.Action, "Task")]);
        await ApplyAsync(factory, analysis);
        await PostUndoCallbackAsync(client, 2, 102);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            Assert.Null((await db.InboxAutomaticApplications.SingleAsync()).UndoneAt);
            Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), m => m.ChatId == 102 &&
                m.Text.Contains("No automatic result", StringComparison.Ordinal));
            var task = await db.Actions.SingleAsync();
            task.Complete(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }
        await PostUndoCallbackAsync(client, 3);
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Single(await saved.Actions.ToListAsync());
        var message = Assert.Single(await saved.TelegramOutboxMessages.ToListAsync(), m =>
            m.Text.Contains("Cannot undo", StringComparison.Ordinal));
        Assert.Contains("Nothing was removed", message.Text, StringComparison.Ordinal);
        Assert.Contains("/result 1", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undo_WithoutSavedDestination_QueuesCompositeSummaryInCommandReply()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var analysis = await SeedAsync(factory, client,
            [Proposal(InboxTargetKind.Action, "Task"), Proposal(InboxTargetKind.Reference, "Note")]);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            await db.TelegramInboxDestinations.ExecuteDeleteAsync();
        }
        await ApplyAsync(factory, analysis);
        await PostTextAsync(client, 2, "/inbox_undo 1");
        await PostTextAsync(client, 2, "/inbox_undo 1");
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var messages = await saved.TelegramOutboxMessages.ToListAsync();
        Assert.Equal(2, messages.Count); // Capture reply and command reply; no inferred proactive destination.
        Assert.Contains("Records affected: 2", Assert.Single(messages, m =>
            m.Text.Contains("Original restored", StringComparison.Ordinal)).Text, StringComparison.Ordinal);
        Assert.NotNull((await saved.InboxAutomaticApplications.SingleAsync()).UndoneAt);
    }

    [Fact]
    public async Task Undo_RussianLocale_RendersReadableButtonAndManualContinuation()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var analysis = await SeedAsync(factory, client, [Proposal(InboxTargetKind.Action, "Task")]);
        await PostTextAsync(client, 2, "/language");
        await PostTextAsync(client, 3, "2");
        await ApplyAsync(factory, analysis);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            var message = await db.TelegramOutboxMessages.SingleAsync(m => m.Id == analysis);
            var keyboard = JsonSerializer.Deserialize<InlineKeyboardMarkup>(message.ReplyMarkupJson!)!;
            Assert.Equal("Отменить весь результат", Assert.Single(keyboard.InlineKeyboard.SelectMany(row => row), b => b.CallbackData == "iu:1").Text);
            Assert.Contains("/undo 1", message.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("Затрагиваемых сущностей", message.Text, StringComparison.Ordinal);
        }
        await PostUndoCallbackAsync(client, 4);
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Contains(await saved.TelegramOutboxMessages.ToListAsync(), m =>
            m.Text.Contains("Исходная запись возвращена", StringComparison.Ordinal) &&
            m.Text.Contains("/process 1", StringComparison.Ordinal));
    }

    private static Task PostUndoCallbackAsync(HttpClient client, int id, long user = 101) => PostAsync(client, new
    {
        update_id = id, callback_query = new
        {
            id = "undo-" + id, chat_instance = "test-chat", data = "iu:1",
            from = new { id = user, is_bot = false, first_name = "Test" },
            message = new
            {
                message_id = 100, date = 1790244000,
                from = new { id = 555, is_bot = true, first_name = "OwnDay" },
                chat = new { id = user, type = "private" }, text = "Created result"
            }
        }
    });
}
