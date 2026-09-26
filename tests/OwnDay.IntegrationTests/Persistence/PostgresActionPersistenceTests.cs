using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OwnDay.Infrastructure.Persistence.Migrations;
using OwnDay.Application.Actions;
using OwnDay.Domain;
using OwnDay.Domain.Actions;
using Action = OwnDay.Domain.Actions.Action;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed class PostgresActionPersistenceTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Migrate_Phase2Rows_PreservesActionsAndSupportsRollback()
    {
        var connectionString = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required for this test.");
        }

        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        var adminOptions = new DbContextOptionsBuilder<OwnDayDbContext>()
            .UseNpgsql(connectionString).Options;
        await using var admin = new OwnDayDbContext(adminOptions);
#pragma warning disable EF1003 // Schema name is generated from a GUID, never user input.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003

        try
        {
            var options = new DbContextOptionsBuilder<OwnDayDbContext>()
                .UseNpgsql(connectionString + ";Search Path=" + schema).Options;
            var createdAt = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
            var completedAt = createdAt.AddHours(2);

            await using (var db = new OwnDayDbContext(options))
            {
                var sqlGenerator = db.GetService<IMigrationsSqlGenerator>();
                Migration[] phase2Migrations =
                [
                    new InitialOperationalState(),
                    new AddOutboxCleanupIndex(),
                    new AddTasks()
                ];
                foreach (var migration in phase2Migrations)
                {
                    foreach (var migrationCommand in sqlGenerator.Generate(migration.UpOperations, db.Model))
                    {
                        await db.Database.ExecuteSqlRawAsync(migrationCommand.CommandText);
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
                        ('202609240001_AddTasks', '10.0.12');
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO tasks (id, user_id, title, status, created_at, completed_at) VALUES (42, 101, 'Active', 0, {createdAt}, NULL), (43, 102, 'Completed', 1, {createdAt}, {completedAt})");
                await db.Database.MigrateAsync();
            }

            await using (var db = new OwnDayDbContext(options))
            {
                var actions = await db.Actions.OrderBy(action => action.Id).ToListAsync();
                Assert.Collection(actions,
                    active =>
                    {
                        Assert.Equal(42, active.Id);
                        Assert.Equal(new UserId(101), active.UserId);
                        Assert.Equal("Active", active.Title);
                        Assert.Equal(ActionStatus.Active, active.Status);
                        Assert.Equal(createdAt, active.CreatedAt);
                        Assert.Null(active.CompletedAt);
                    },
                    completed =>
                    {
                        Assert.Equal(43, completed.Id);
                        Assert.Equal(new UserId(102), completed.UserId);
                        Assert.Equal("Completed", completed.Title);
                        Assert.Equal(ActionStatus.Completed, completed.Status);
                        Assert.Equal(createdAt, completed.CreatedAt);
                        Assert.Equal(completedAt, completed.CompletedAt);
                    });

                var service = new ActionService(new EfActionStore(db), TimeProvider.System);
                Assert.Equal(CompleteActionResult.Completed,
                    await service.CompleteAsync(new UserId(101), 42, CancellationToken.None));

                var sqlGenerator = db.GetService<IMigrationsSqlGenerator>();
                foreach (var migrationCommand in sqlGenerator.Generate(
                    new RenameTasksToActions().DownOperations, db.Model))
                {
                    await db.Database.ExecuteSqlRawAsync(migrationCommand.CommandText);
                }

                await db.Database.OpenConnectionAsync();
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT count(*) FROM tasks WHERE id IN (42, 43)";
                Assert.Equal(2L, await command.ExecuteScalarAsync());
            }
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name used above.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task MigrateAndUseActions_NewDbContext_RetainsActionState()
    {
        var connectionString = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required for this test.");
        }

        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        var adminOptions = new DbContextOptionsBuilder<OwnDayDbContext>()
            .UseNpgsql(connectionString).Options;
        await using var admin = new OwnDayDbContext(adminOptions);
#pragma warning disable EF1003 // Schema name is generated from a GUID, never user input.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003

        try
        {
            var options = new DbContextOptionsBuilder<OwnDayDbContext>()
                .UseNpgsql(connectionString + ";Search Path=" + schema).Options;
            await using (var db = new OwnDayDbContext(options))
            {
                await db.Database.MigrateAsync();
                var service = new ActionService(new EfActionStore(db), TimeProvider.System);
                var first = await service.AddAsync(new UserId(1), "First action", CancellationToken.None);
                await service.AddAsync(new UserId(2), "Other user's action", CancellationToken.None);
                Assert.True(first.Id > 0);
            }

            await using (var db = new OwnDayDbContext(options))
            {
                var service = new ActionService(new EfActionStore(db), TimeProvider.System);
                var actions = await service.GetActiveAsync(new UserId(1), CancellationToken.None);
                var first = Assert.Single(actions);
                Assert.Equal("First action", first.Title);
                Assert.Equal(CompleteActionResult.NotFound,
                    await service.CompleteAsync(new UserId(2), first.Id, CancellationToken.None));
                Assert.Equal(CompleteActionResult.Completed,
                    await service.CompleteAsync(new UserId(1), first.Id, CancellationToken.None));
            }

            await using (var db = new OwnDayDbContext(options))
            {
                var service = new ActionService(new EfActionStore(db), TimeProvider.System);
                Assert.Empty(await service.GetActiveAsync(new UserId(1), CancellationToken.None));
                Assert.Single(await service.GetActiveAsync(new UserId(2), CancellationToken.None));
                Assert.Equal(ActionStatus.Completed,
                    (await db.Actions.SingleAsync(action => action.UserId == new UserId(1))).Status);
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
