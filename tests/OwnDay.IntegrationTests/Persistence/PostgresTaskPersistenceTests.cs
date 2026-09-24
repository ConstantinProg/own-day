using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Tasks;
using OwnDay.Domain.Tasks;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed class PostgresTaskPersistenceTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task MigrateAndUseTasks_NewDbContext_RetainsTaskState()
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
                var service = new TaskService(new EfTaskStore(db), TimeProvider.System);
                var first = await service.AddAsync(new UserId(1), "First task", CancellationToken.None);
                await service.AddAsync(new UserId(2), "Other user's task", CancellationToken.None);
                Assert.True(first.Id > 0);
            }

            await using (var db = new OwnDayDbContext(options))
            {
                var service = new TaskService(new EfTaskStore(db), TimeProvider.System);
                var tasks = await service.GetActiveAsync(new UserId(1), CancellationToken.None);
                var first = Assert.Single(tasks);
                Assert.Equal("First task", first.Title);
                Assert.Equal(CompleteTaskResult.NotFound,
                    await service.CompleteAsync(new UserId(2), first.Id, CancellationToken.None));
                Assert.Equal(CompleteTaskResult.Completed,
                    await service.CompleteAsync(new UserId(1), first.Id, CancellationToken.None));
            }

            await using (var db = new OwnDayDbContext(options))
            {
                var service = new TaskService(new EfTaskStore(db), TimeProvider.System);
                Assert.Empty(await service.GetActiveAsync(new UserId(1), CancellationToken.None));
                Assert.Single(await service.GetActiveAsync(new UserId(2), CancellationToken.None));
                Assert.Equal(TaskItemStatus.Completed,
                    (await db.Tasks.SingleAsync(task => task.UserId == new UserId(1))).Status);
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
