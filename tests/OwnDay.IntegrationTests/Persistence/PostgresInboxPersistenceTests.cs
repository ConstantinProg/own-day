using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using OwnDay.Application.Actions;
using OwnDay.Application.Inbox;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Persistence.Migrations;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed class PostgresInboxPersistenceTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Migrate_PreviousSchemaWithData_PreservesHistoricalRecords()
    {
        var connectionString = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required for this test.");
        }

        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        var adminOptions = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connectionString).Options;
        await using var admin = new OwnDayDbContext(adminOptions);
#pragma warning disable EF1003 // Schema name is generated from a GUID, never user input.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003

        try
        {
            var options = new DbContextOptionsBuilder<OwnDayDbContext>()
                .UseNpgsql(connectionString + ";Search Path=" + schema).Options;
            var owner = new UserId(201);
            long projectId;
            long actionId;
            long ideaId;
            long noteId;
            long waitingId;
            var createdAt = new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc);

            await using (var db = new OwnDayDbContext(options))
            {
                var sqlGenerator = db.GetService<IMigrationsSqlGenerator>();
                Migration[] previousMigrations =
                [
                    new InitialOperationalState(),
                    new AddOutboxCleanupIndex(),
                    new AddTasks(),
                    new RenameTasksToActions(),
                    new AddStructuredItems()
                ];
                foreach (var migration in previousMigrations)
                {
                    foreach (var command in sqlGenerator.Generate(migration.UpOperations, db.Model))
                    {
                        await db.Database.ExecuteSqlRawAsync(command.CommandText);
                    }
                }

                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE "__EFMigrationsHistory" (
                        "MigrationId" character varying(150) NOT NULL PRIMARY KEY,
                        "ProductVersion" character varying(32) NOT NULL
                    );
                    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES
                        ('202609170001_InitialOperationalState', '10.0.12'),
                        ('202609170002_AddOutboxCleanupIndex', '10.0.12'),
                        ('202609240001_AddTasks', '10.0.12'),
                        ('202609260001_RenameTasksToActions', '10.0.12'),
                        ('202609260002_AddStructuredItems', '10.0.12');
                    """);
                var clock = new FixedTimeProvider(createdAt);
                var structured = new StructuredItemService(new EfStructuredItemStore(db), clock);
                var actions = new ActionService(new EfActionStore(db), clock);
                var project = await structured.CreateProjectAsync(owner, "Historical project", CancellationToken.None);
                projectId = project.Id;
                var action = await actions.AddAsync(owner, "Historical action", projectId, CancellationToken.None);
                actionId = action.Id;
                ideaId = (await structured.CreateSomedayMaybeAsync(owner, "Historical idea", projectId, CancellationToken.None)).Id;
                noteId = (await structured.CreateReferenceAsync(owner, "Historical note", projectId, CancellationToken.None)).Id;
                waitingId = (await structured.CreateWaitingForAsync(owner, "Historical wait", "Source", projectId, CancellationToken.None)).Id;
                Assert.True(action.Complete(createdAt.AddMinutes(1)));
                await db.SaveChangesAsync();

                await db.Database.MigrateAsync();
            }

            await using (var db = new OwnDayDbContext(options))
            {
                var project = await db.Projects.SingleAsync(item => item.Id == projectId);
                Assert.Equal(owner, project.UserId);
                Assert.Equal("Historical project", project.Title);
                var action = await db.Actions.SingleAsync(item => item.Id == actionId);
                Assert.Equal(owner, action.UserId);
                Assert.Equal("Historical action", action.Title);
                Assert.Equal(createdAt, action.CreatedAt);
                Assert.Equal(createdAt.AddMinutes(1), action.CompletedAt);
                Assert.Equal(projectId, action.ProjectId);
                Assert.Equal(projectId, (await db.SomedayMaybes.SingleAsync(item => item.Id == ideaId)).ProjectId);
                Assert.Equal(projectId, (await db.References.SingleAsync(item => item.Id == noteId)).ProjectId);
                Assert.Equal(projectId, (await db.WaitingFors.SingleAsync(item => item.Id == waitingId)).ProjectId);
                Assert.Empty(await db.InboxItems.ToListAsync());
            }
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name used above.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task CaptureAndCreate_RestartedContext_PreservesOwnedDataAndConstraints()
    {
        var connectionString = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required for this test.");
        }

        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        var adminOptions = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connectionString).Options;
        await using var admin = new OwnDayDbContext(adminOptions);
#pragma warning disable EF1003 // Schema name is generated from a GUID, never user input.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003

        try
        {
            var options = new DbContextOptionsBuilder<OwnDayDbContext>()
                .UseNpgsql(connectionString + ";Search Path=" + schema).Options;
            var alice = new UserId(101);
            var bob = new UserId(102);
            var original = "  raw\n" + new string('Ж', 2000);
            long inboxId;
            long projectId;
            long actionId;

            await using (var db = new OwnDayDbContext(options))
            {
                await db.Database.MigrateAsync();
                var inbox = new InboxCaptureService(new EfInboxItemStore(db), TimeProvider.System);
                var structured = new StructuredItemService(new EfStructuredItemStore(db), TimeProvider.System);
                var actions = new ActionService(new EfActionStore(db), TimeProvider.System);
                var captured = await inbox.CaptureAsync(alice, original, CancellationToken.None);
                inboxId = captured.Id;
                await inbox.CaptureAsync(bob, "private", CancellationToken.None);
                var project = await structured.CreateProjectAsync(alice, "Outcome", CancellationToken.None);
                projectId = project.Id;
                actionId = (await actions.AddAsync(alice, "Linked", projectId, CancellationToken.None)).Id;
                await structured.CreateSomedayMaybeAsync(alice, "Idea", projectId, CancellationToken.None);
                await structured.CreateReferenceAsync(alice, "Note", projectId, CancellationToken.None);
                await structured.CreateWaitingForAsync(alice, "Reply", "Bob", projectId, CancellationToken.None);
            }

            await using (var db = new OwnDayDbContext(options))
            {
                var inbox = new InboxCaptureService(new EfInboxItemStore(db), TimeProvider.System);
                var structured = new StructuredItemService(new EfStructuredItemStore(db), TimeProvider.System);
                var actions = new ActionService(new EfActionStore(db), TimeProvider.System);
                var item = Assert.Single(await inbox.GetActiveAsync(alice, CancellationToken.None));
                Assert.Equal(inboxId, item.Id);
                Assert.Equal(original, item.OriginalText);
                Assert.Null(await inbox.FindAsync(bob, inboxId, CancellationToken.None));
                Assert.Null(await structured.FindProjectAsync(bob, projectId, CancellationToken.None));
                Assert.Equal(projectId, Assert.Single(await structured.GetActiveProjectsAsync(alice, CancellationToken.None)).Id);
                Assert.Equal(projectId, Assert.Single(await actions.GetActiveAsync(alice, CancellationToken.None)).ProjectId);
                Assert.Equal(actionId, Assert.Single(await actions.GetActiveAsync(alice, CancellationToken.None)).Id);
                Assert.Equal(projectId, Assert.Single(await structured.GetActiveSomedayMaybesAsync(alice, CancellationToken.None)).ProjectId);
                Assert.Equal(projectId, Assert.Single(await structured.GetActiveReferencesAsync(alice, CancellationToken.None)).ProjectId);
                Assert.Equal(projectId, Assert.Single(await structured.GetActiveWaitingForsAsync(alice, CancellationToken.None)).ProjectId);

                Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(async () =>
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE actions SET user_id = {bob.Value} WHERE id = {actionId}"))).SqlState);
                Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(async () =>
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inbox_items SET status = 1 WHERE id = {inboxId}"))).SqlState);
                Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(async () =>
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inbox_items SET status = 1, processed_at = captured_at WHERE id = {inboxId}"))).SqlState);
                Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(async () =>
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inbox_items SET original_text = {' '} WHERE id = {inboxId}"))).SqlState);
            }
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name used above.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }
}
