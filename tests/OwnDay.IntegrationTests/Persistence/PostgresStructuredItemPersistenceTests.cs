using Microsoft.EntityFrameworkCore;
using Npgsql;
using OwnDay.Application.StructuredItems;
using OwnDay.Application.Actions;
using OwnDay.Domain;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed class PostgresStructuredItemPersistenceTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task MigrateAndPersist_OwnedStructuredItems_EnforcesProjectOwnershipAndLifecycle()
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
            await using var db = new OwnDayDbContext(options);
            await db.Database.MigrateAsync();
            var service = new StructuredItemService(new EfStructuredItemStore(db), TimeProvider.System);
            var actions = new ActionService(new EfActionStore(db), TimeProvider.System);
            var alice = new UserId(101);
            var bob = new UserId(102);
            var token = CancellationToken.None;

            var project = await service.CreateProjectAsync(alice, "Outcome", token);
            var plainAction = await actions.AddAsync(alice, "Standalone", token);
            var action = await actions.AddAsync(alice, "Linked", project.Id, token);
            var idea = await service.CreateSomedayMaybeAsync(alice, "Idea", project.Id, token);
            var note = await service.CreateReferenceAsync(alice, "Note", project.Id, token);
            var waiting = await service.CreateWaitingForAsync(alice, "Reply", "Bob", project.Id, token);

            db.ChangeTracker.Clear();
            Assert.Null((await db.Actions.SingleAsync(item => item.Id == plainAction.Id)).ProjectId);
            Assert.Equal(project.Id, (await db.Actions.SingleAsync(item => item.Id == action.Id)).ProjectId);
            Assert.Equal(project.Id, (await db.SomedayMaybes.SingleAsync(item => item.Id == idea.Id)).ProjectId);
            Assert.Equal(project.Id, (await db.References.SingleAsync(item => item.Id == note.Id)).ProjectId);
            Assert.Equal(project.Id, (await db.WaitingFors.SingleAsync(item => item.Id == waiting.Id)).ProjectId);
            Assert.Equal("Outcome", (await db.Projects.SingleAsync(item => item.Id == project.Id)).Title);

            await Assert.ThrowsAsync<ArgumentException>(() => actions.AddAsync(bob, "Foreign", project.Id, token));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateReferenceAsync(alice, "Unknown", project.Id + 999, token));

            Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE actions SET user_id = {bob.Value} WHERE id = {action.Id}"))).SqlState);
            Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE someday_maybes SET user_id = {bob.Value} WHERE id = {idea.Id}"))).SqlState);
            Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"references\" SET user_id = {bob.Value} WHERE id = {note.Id}"))).SqlState);
            Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE waiting_fors SET user_id = {bob.Value} WHERE id = {waiting.Id}"))).SqlState);
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE projects SET status = 1 WHERE id = {project.Id}"))).SqlState);
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE someday_maybes SET status = 1 WHERE id = {idea.Id}"))).SqlState);
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"references\" SET status = 1 WHERE id = {note.Id}"))).SqlState);
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE waiting_fors SET status = 1 WHERE id = {waiting.Id}"))).SqlState);

            Assert.Equal(2, await db.Actions.CountAsync());
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name used above.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }
}
