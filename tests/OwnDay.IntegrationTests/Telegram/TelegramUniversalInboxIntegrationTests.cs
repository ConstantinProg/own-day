using System.Net;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Inbox;
using OwnDay.Domain.Inbox;
using OwnDay.Domain;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramUniversalInboxIntegrationTests
{
    [Fact]
    public async Task Post_ReopenDraft_ResetsPreviousChoicesAndKeepsInboxItemsActive()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, 101, "first");
        await PostAsync(client, 2, 101, "second");
        await PostAsync(client, 3, 101, "/inbox 1");
        await PostAsync(client, 4, 101, "5");
        await PostAsync(client, 5, 101, "edited");
        await PostAsync(client, 6, 101, "source");
        await PostAsync(client, 7, 101, "0");
        await PostAsync(client, 8, 101, "/inbox 2");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var draft = await db.TelegramInboxDrafts.SingleAsync();
        Assert.Equal(2, draft.InboxItemId);
        Assert.Equal(InboxDraftStep.Target, draft.Step);
        Assert.Null(draft.TargetKind);
        Assert.Null(draft.Text);
        Assert.Null(draft.Source);
        Assert.Null(draft.ProjectId);
        Assert.All(await db.InboxItems.ToListAsync(), item => Assert.Equal(InboxItemStatus.Active, item.Status));
        Assert.Empty(await db.WaitingFors.ToListAsync());
    }

    [Fact]
    public async Task Post_ReopenReplySaveFails_RollsBackDraftChangesAndDeduplication()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, 101, "first");
        await PostAsync(client, 2, 101, "second");
        await PostAsync(client, 3, 101, "/inbox 1");
        await PostAsync(client, 4, 101, "1");
        using (var setupScope = factory.Services.CreateScope())
        {
            var setup = setupScope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_outbox BEFORE INSERT ON telegram_outbox_messages
                BEGIN SELECT RAISE(ABORT, 'Simulated storage failure'); END;
                """);
        }

        await PostAsync(client, 5, 101, "/inbox 2", expectedStatus: HttpStatusCode.InternalServerError);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var draft = await db.TelegramInboxDrafts.SingleAsync();
        Assert.Equal(1, draft.InboxItemId);
        Assert.Equal(InboxDraftStep.Text, draft.Step);
        Assert.Equal(InboxTargetKind.Action, draft.TargetKind);
        Assert.Equal("first", draft.Text);
        Assert.False(await db.ProcessedTelegramUpdates.AnyAsync(update => update.UpdateId == 5));
        Assert.Equal(4, await db.TelegramOutboxMessages.CountAsync());
    }

    [Theory]
    [InlineData("/add one", "Action")]
    [InlineData("/project one", "Project")]
    [InlineData("/idea one", "Idea")]
    [InlineData("/note one", "Note")]
    [InlineData("/wait one", "Waiting")]
    public async Task Post_DirectCapture_CreatesOnlyRequestedTypeOnce(string text, string kind)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, 101, text);
        await PostAsync(client, 1, 101, text);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(kind == "Action" ? 1 : 0, await db.Actions.CountAsync());
        Assert.Equal(kind == "Project" ? 1 : 0, await db.Projects.CountAsync());
        Assert.Equal(kind == "Idea" ? 1 : 0, await db.SomedayMaybes.CountAsync());
        Assert.Equal(kind == "Note" ? 1 : 0, await db.References.CountAsync());
        Assert.Equal(kind == "Waiting" ? 1 : 0, await db.WaitingFors.CountAsync());
        Assert.Empty(await db.InboxItems.ToListAsync());
        var reply = Assert.Single(await db.TelegramOutboxMessages.ToListAsync());
        Assert.Contains("one", reply.Text, StringComparison.Ordinal);
        Assert.Equal(new UserId(101), kind switch
        {
            "Action" => (await db.Actions.SingleAsync()).UserId,
            "Project" => (await db.Projects.SingleAsync()).UserId,
            "Idea" => (await db.SomedayMaybes.SingleAsync()).UserId,
            "Note" => (await db.References.SingleAsync()).UserId,
            _ => (await db.WaitingFors.SingleAsync()).UserId
        });
    }

    [Theory]
    [InlineData("/add")]
    [InlineData("/project")]
    [InlineData("/idea")]
    [InlineData("/note")]
    [InlineData("/wait")]
    public async Task Post_EmptyDirectCapture_CreatesNothing(string command)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, 101, command);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(0, await db.Actions.CountAsync() + await db.Projects.CountAsync() +
            await db.SomedayMaybes.CountAsync() + await db.References.CountAsync() + await db.WaitingFors.CountAsync());
        Assert.Empty(await db.InboxItems.ToListAsync());
    }

    [Fact]
    public async Task Post_OrdinaryText_CapturesExactTextOnce()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, 101, "  разобраться с mapping  ");
        await PostAsync(client, 1, 101, "  разобраться с mapping  ");
        await PostAsync(client, 2, 101, "/unknown");
        await PostAsync(client, 3, 101, "/add@OtherBot ignored");
        await PostAsync(client, 4, 101, "group text", "group");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var item = Assert.Single(await db.InboxItems.ToListAsync());
        Assert.Equal("  разобраться с mapping  ", item.OriginalText);
        Assert.Equal(InboxItemStatus.Active, item.Status);
        Assert.Empty(await db.Actions.ToListAsync());
        Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), reply => reply.Text == "Saved to your inbox. Open /inbox to process it.");
    }

    [Theory]
    [InlineData(1, "Action", InboxTargetKind.Action)]
    [InlineData(2, "Project", InboxTargetKind.Project)]
    [InlineData(3, "Idea", InboxTargetKind.SomedayMaybe)]
    [InlineData(4, "Note", InboxTargetKind.Reference)]
    [InlineData(5, "Waiting", InboxTargetKind.WaitingFor)]
    public async Task Post_InboxFlow_ProcessesChosenTargetWithProvenance(int choice, string kind, InboxTargetKind expectedTarget)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var original = choice == 1 ? "позвонить сергею насчет договора" : "original";
        var edited = choice == 1 ? "Позвонить Сергею по поводу договора" : "edited";
        await PostAsync(client, 1, 101, "/project Main");
        await PostAsync(client, 2, 101, original);
        await PostAsync(client, 3, 101, "/inbox");
        await PostAsync(client, 4, 101, "/inbox 1");
        await PostAsync(client, 5, 101, choice.ToString());
        await PostAsync(client, 6, 101, edited);
        var next = 7L;
        if (choice == 5) await PostAsync(client, next++, 101, "source");
        if (choice != 2) await PostAsync(client, next++, 101, "1");
        var confirmation = choice == 1 ? "да" : "yes";
        await PostAsync(client, next, 101, confirmation);
        await PostAsync(client, next, 101, confirmation);
        await PostAsync(client, next + 1, 101, "/inbox");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var inbox = Assert.Single(await db.InboxItems.ToListAsync());
        Assert.Equal(original, inbox.OriginalText);
        Assert.Equal(InboxItemStatus.Processed, inbox.Status);
        Assert.Equal(expectedTarget, inbox.TargetKind);
        Assert.NotNull(inbox.TargetId);
        Assert.NotNull(inbox.ProcessedAt);
        Assert.True(inbox.ProcessedAt >= inbox.CapturedAt);
        Assert.Empty(await db.TelegramInboxDrafts.ToListAsync());
        Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), reply => reply.ChatId == 101 && reply.Text == "Inbox: empty.");
        Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), reply => reply.ChatId == 101 && reply.Text.Contains("Choose a type:\n1 — Task", StringComparison.Ordinal));
        Assert.Equal(kind == "Action" ? 1 : 0, await db.Actions.CountAsync());
        Assert.Equal(kind == "Project" ? 2 : 1, await db.Projects.CountAsync());
        Assert.Equal(kind == "Idea" ? 1 : 0, await db.SomedayMaybes.CountAsync());
        Assert.Equal(kind == "Note" ? 1 : 0, await db.References.CountAsync());
        Assert.Equal(kind == "Waiting" ? 1 : 0, await db.WaitingFors.CountAsync());
        if (choice == 1)
        {
            var action = await db.Actions.SingleAsync();
            Assert.Equal(1, action.ProjectId);
            Assert.Equal(edited, action.Title);
            Assert.Equal(action.Id, inbox.TargetId);
        }
        if (choice == 5) Assert.Equal("source", (await db.WaitingFors.SingleAsync()).Source);
    }

    [Theory]
    [InlineData("/cancel", InboxItemStatus.Active)]
    [InlineData("/discard", InboxItemStatus.Discarded)]
    public async Task Post_TerminateFlow_LeavesExpectedInboxState(string command, InboxItemStatus status)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, 101, "original");
        await PostAsync(client, 2, 101, "/inbox 1");
        await PostAsync(client, 3, 101, command);
        await PostAsync(client, 4, 101, "/inbox");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(status, (await db.InboxItems.SingleAsync()).Status);
        Assert.Empty(await db.TelegramInboxDrafts.ToListAsync());
        Assert.Empty(await db.Actions.ToListAsync());
        var listed = (await db.TelegramOutboxMessages.OrderBy(reply => reply.CreatedAt).ToListAsync()).Last().Text;
        Assert.Equal(status == InboxItemStatus.Active ? "Inbox\n#1 original — /inbox 1" : "Inbox: empty.", listed);
    }

    [Fact]
    public async Task Post_CommandDuringFlow_PreservesDraftAndCreatesDirectAction()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, 101, "original");
        await PostAsync(client, 2, 101, "/inbox 1");
        await PostAsync(client, 3, 101, "/add direct");
        await PostAsync(client, 4, 101, "2");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal("direct", (await db.Actions.SingleAsync()).Title);
        Assert.Equal(InboxDraftStep.Text, (await db.TelegramInboxDrafts.SingleAsync()).Step);
        Assert.Equal(InboxItemStatus.Active, (await db.InboxItems.SingleAsync()).Status);
    }

    [Fact]
    public async Task Post_DraftAcrossScopes_ContinuesAfterRestartEquivalent()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, 101, "original");
        await PostAsync(client, 2, 101, "/inbox 1");
        await PostAsync(client, 3, 101, "2");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            Assert.Equal(InboxDraftStep.Text, (await db.TelegramInboxDrafts.AsNoTracking().SingleAsync()).Step);
        }

        await PostAsync(client, 4, 101, "edited");
        await PostAsync(client, 5, 101, "yes");
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal("edited", (await verify.Projects.SingleAsync()).Title);
        Assert.Equal(InboxItemStatus.Processed, (await verify.InboxItems.SingleAsync()).Status);
    }

    [Fact]
    public async Task Post_ExpiredDraft_LeavesInboxActiveAndCanRestart()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, 101, "original");
        await PostAsync(client, 2, 101, "/inbox 1");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            await db.TelegramInboxDrafts.ExecuteUpdateAsync(setters => setters
                .SetProperty(draft => draft.ExpiresAt, DateTime.UtcNow.AddDays(-1)));
        }

        await PostAsync(client, 3, 101, "1");
        await PostAsync(client, 4, 101, "/inbox 1");
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var inboxItems = await verify.InboxItems.OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(2, inboxItems.Count);
        Assert.All(inboxItems, item => Assert.Equal(InboxItemStatus.Active, item.Status));
        Assert.Equal("original", inboxItems[0].OriginalText);
        Assert.Equal("1", inboxItems[1].OriginalText);
        Assert.Empty(await verify.Actions.ToListAsync());
        Assert.Equal(InboxDraftStep.Target, (await verify.TelegramInboxDrafts.SingleAsync()).Step);
        Assert.Contains(await verify.TelegramOutboxMessages.ToListAsync(), reply => reply.Text.Contains("draft expired", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_Lists_ShowOwnedRecordsAndNavigation()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var commands = new[] { "/add task", "/project project", "/idea idea", "/note note", "/wait waiting", "inbox" };
        for (var index = 0; index < commands.Length; index++)
        {
            await PostAsync(client, index + 1, 101, commands[index]);
            await PostAsync(client, index + 101, 202, index switch
            {
                0 => "/add foreign",
                1 => "/project foreign",
                2 => "/idea foreign",
                3 => "/note foreign",
                4 => "/wait foreign",
                _ => "foreign"
            });
        }

        var lists = new[] { "/tasks", "/projects", "/ideas", "/notes", "/waiting", "/inbox", "/list" };
        for (var index = 0; index < lists.Length; index++)
        {
            await PostAsync(client, index + 201, 101, lists[index]);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var replies = await db.TelegramOutboxMessages.Where(reply => reply.ChatId == 101)
            .OrderBy(reply => reply.CreatedAt).ToListAsync();
        var listing = replies.TakeLast(7).Select(reply => reply.Text).ToArray();
        Assert.Contains("#1 task", listing[0], StringComparison.Ordinal);
        Assert.Contains("#1 project", listing[1], StringComparison.Ordinal);
        Assert.Contains("#1 idea", listing[2], StringComparison.Ordinal);
        Assert.Contains("#1 note", listing[3], StringComparison.Ordinal);
        Assert.Contains("#1 waiting", listing[4], StringComparison.Ordinal);
        Assert.Contains("/inbox 1", listing[5], StringComparison.Ordinal);
        Assert.Contains("/waiting", listing[6], StringComparison.Ordinal);
        Assert.DoesNotContain(listing, value => value.Contains("foreign", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_ForeignInboxAndProject_AreUnavailable()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, 1, 202, "foreign inbox");
        await PostAsync(client, 2, 202, "/project foreign project");
        await PostAsync(client, 3, 101, "/inbox 1");
        await PostAsync(client, 4, 101, "own inbox");
        await PostAsync(client, 5, 101, "/inbox 2");
        await PostAsync(client, 6, 101, "1");
        await PostAsync(client, 7, 101, ".");
        await PostAsync(client, 8, 101, "1");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(InboxDraftStep.Project, (await db.TelegramInboxDrafts.SingleAsync()).Step);
        Assert.Equal(InboxItemStatus.Active, (await db.InboxItems.SingleAsync(item => item.UserId == new UserId(101))).Status);
        Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), reply => reply.ChatId == 101 && reply.Text.Contains("Project not found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_LongProjectList_SplitsBelowTelegramLimit()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        for (var index = 1; index <= 30; index++)
        {
            await PostAsync(client, index, 101, "/project " + new string('x', 200));
        }

        await PostAsync(client, 31, 101, "/projects");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var replies = await db.TelegramOutboxMessages.OrderBy(reply => reply.CreatedAt).ToListAsync();
        var listReplies = replies.Skip(30).ToList();
        Assert.True(listReplies.Count > 1);
        Assert.All(listReplies, reply => Assert.InRange(reply.Text.Length, 1, 4096));
        Assert.Contains("#30", string.Join('\n', listReplies.Select(reply => reply.Text)), StringComparison.Ordinal);
    }

    private static async Task PostAsync(HttpClient client, long updateId, long userId, string text,
        string chatType = "private", HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(new
        {
            update_id = updateId,
            message = new
            {
                message_id = updateId,
                date = 1790244000,
                from = new { id = userId, is_bot = false, first_name = "Test" },
                chat = new { id = userId, type = chatType },
                text
            }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.TryAddWithoutValidation("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
    }
}
