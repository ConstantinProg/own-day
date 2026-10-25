using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Actions;
using OwnDay.Application.Inbox;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed class InboxApplicationPersistenceTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationQueries_OwnerAndStatus_ReturnOnlyActiveRecordsInCreationOrder(bool reverseCreationTime)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OwnDayDbContext>().UseSqlite(connection).Options;
        var alice = new UserId(11);
        var bob = new UserId(12);
        var clock = new TestTimeProvider(reverseCreationTime);

        await using (var db = new OwnDayDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var inbox = new InboxCaptureService(new EfInboxItemStore(db), clock);
            var structured = new StructuredItemService(new EfStructuredItemStore(db), clock);
            var actions = new ActionService(new EfActionStore(db), clock);

            Assert.Empty(await inbox.GetActiveAsync(alice, CancellationToken.None));
            Assert.Empty(await structured.GetActiveProjectsAsync(alice, CancellationToken.None));
            Assert.Empty(await structured.GetActiveSomedayMaybesAsync(alice, CancellationToken.None));
            Assert.Empty(await structured.GetActiveReferencesAsync(alice, CancellationToken.None));
            Assert.Empty(await structured.GetActiveWaitingForsAsync(alice, CancellationToken.None));

            var first = await inbox.CaptureAsync(alice, "  original\n" + new string('x', 500), CancellationToken.None);
            var second = await inbox.CaptureAsync(alice, "second", CancellationToken.None);
            var processed = await inbox.CaptureAsync(alice, "processed", CancellationToken.None);
            var discarded = await inbox.CaptureAsync(alice, "discarded", CancellationToken.None);
            await inbox.CaptureAsync(bob, "private", CancellationToken.None);
            Assert.True(processed.Process(InboxTargetKind.Action, 99, Now.AddMinutes(1)));
            Assert.True(discarded.Discard(Now.AddMinutes(1)));

            var project = await structured.CreateProjectAsync(alice, "Project", CancellationToken.None);
            var nextProject = await structured.CreateProjectAsync(alice, "Next project", CancellationToken.None);
            var terminalProject = await structured.CreateProjectAsync(alice, "Completed", CancellationToken.None);
            Assert.True(terminalProject.Complete(Now.AddMinutes(1)));
            await structured.CreateProjectAsync(bob, "Private project", CancellationToken.None);

            var action = await actions.AddAsync(alice, "Action", project.Id, CancellationToken.None);
            var nextAction = await actions.AddAsync(alice, "Next action", CancellationToken.None);
            var done = await actions.AddAsync(alice, "Done", CancellationToken.None);
            await actions.AddAsync(bob, "Private action", CancellationToken.None);
            Assert.True(done.Complete(Now.AddMinutes(1)));

            var idea = await structured.CreateSomedayMaybeAsync(alice, "Idea", project.Id, CancellationToken.None);
            var nextIdea = await structured.CreateSomedayMaybeAsync(alice, "Next idea", null, CancellationToken.None);
            var oldIdea = await structured.CreateSomedayMaybeAsync(alice, "Old idea", null, CancellationToken.None);
            await structured.CreateSomedayMaybeAsync(bob, "Private idea", null, CancellationToken.None);
            Assert.True(oldIdea.Archive(Now.AddMinutes(1)));

            var note = await structured.CreateReferenceAsync(alice, "Note", project.Id, CancellationToken.None);
            var nextNote = await structured.CreateReferenceAsync(alice, "Next note", null, CancellationToken.None);
            var oldNote = await structured.CreateReferenceAsync(alice, "Old note", null, CancellationToken.None);
            await structured.CreateReferenceAsync(bob, "Private note", null, CancellationToken.None);
            Assert.True(oldNote.Archive(Now.AddMinutes(1)));

            var waiting = await structured.CreateWaitingForAsync(alice, "Reply", "Bob", project.Id, CancellationToken.None);
            var nextWaiting = await structured.CreateWaitingForAsync(alice, "Next reply", null, null, CancellationToken.None);
            var resolved = await structured.CreateWaitingForAsync(alice, "Old reply", null, null, CancellationToken.None);
            await structured.CreateWaitingForAsync(bob, "Private waiting", null, null, CancellationToken.None);
            Assert.True(resolved.Resolve(Now.AddMinutes(1)));
            await db.SaveChangesAsync();

            long[] ExpectedOrder(long firstId, long secondId) =>
                reverseCreationTime ? [secondId, firstId] : [firstId, secondId];

            Assert.Equal(ExpectedOrder(first.Id, second.Id), (await inbox.GetActiveAsync(alice, CancellationToken.None)).Select(item => item.Id));
            Assert.Equal(ExpectedOrder(project.Id, nextProject.Id), (await structured.GetActiveProjectsAsync(alice, CancellationToken.None)).Select(item => item.Id));
            Assert.Equal(ExpectedOrder(action.Id, nextAction.Id), (await actions.GetActiveAsync(alice, CancellationToken.None)).Select(item => item.Id));
            Assert.Equal(ExpectedOrder(idea.Id, nextIdea.Id), (await structured.GetActiveSomedayMaybesAsync(alice, CancellationToken.None)).Select(item => item.Id));
            Assert.Equal(ExpectedOrder(note.Id, nextNote.Id), (await structured.GetActiveReferencesAsync(alice, CancellationToken.None)).Select(item => item.Id));
            Assert.Equal(ExpectedOrder(waiting.Id, nextWaiting.Id), (await structured.GetActiveWaitingForsAsync(alice, CancellationToken.None)).Select(item => item.Id));
            Assert.Null(await inbox.FindAsync(bob, first.Id, CancellationToken.None));
            Assert.Null(await structured.FindProjectAsync(bob, project.Id, CancellationToken.None));
        }

        await using (var db = new OwnDayDbContext(options))
        {
            var inbox = new InboxCaptureService(new EfInboxItemStore(db), clock);
            var structured = new StructuredItemService(new EfStructuredItemStore(db), clock);
            var own = await inbox.GetActiveAsync(alice, CancellationToken.None);
            Assert.Equal(2, own.Count);
            var original = Assert.Single(own, item => item.OriginalText == "  original\n" + new string('x', 500));
            Assert.Equal(Now, original.CapturedAt);
            Assert.All(own, item => Assert.Equal(InboxItemStatus.Active, item.Status));
            Assert.Equal("Bob", Assert.Single(await structured.GetActiveWaitingForsAsync(alice, CancellationToken.None),
                item => item.Description == "Reply").Source);
            Assert.Equal(1, await db.Actions.CountAsync(item => item.UserId == alice && item.ProjectId != null));
        }
    }

    private sealed class TestTimeProvider(bool reverseCreationTime) : TimeProvider
    {
        private int _calls;

        public override DateTimeOffset GetUtcNow() => new(reverseCreationTime ? Now.AddMinutes(-_calls++) : Now);
    }
}
