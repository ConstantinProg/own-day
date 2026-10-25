using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed class PostgresTelegramInboxDraftTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Draft_AcrossContexts_PersistsAndRejectsStaleVersion()
    {
        var connectionString = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required for this test.");
        }

        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new OwnDayDbContext(new DbContextOptionsBuilder<OwnDayDbContext>()
            .UseNpgsql(connectionString).Options);
#pragma warning disable EF1003 // Schema name is generated from a GUID.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003
        try
        {
            var options = new DbContextOptionsBuilder<OwnDayDbContext>()
                .UseNpgsql(connectionString + ";Search Path=" + schema).Options;
            await using (var setup = new OwnDayDbContext(options))
            {
                await setup.Database.MigrateAsync();
                setup.TelegramInboxDrafts.Add(new InboxDraft
                {
                    UserId = 101,
                    InboxItemId = 5,
                    Step = InboxDraftStep.Target,
                    ExpiresAt = DateTime.UtcNow.AddHours(24)
                });
                await setup.SaveChangesAsync();
            }

            await using var first = new OwnDayDbContext(options);
            await using var stale = new OwnDayDbContext(options);
            var current = await first.TelegramInboxDrafts.SingleAsync();
            var old = await stale.TelegramInboxDrafts.SingleAsync();
            Assert.Equal(InboxDraftStep.Target, current.Step);
            current.Step = InboxDraftStep.Text;
            current.Version++;
            await first.SaveChangesAsync();
            old.Step = InboxDraftStep.Confirm;
            old.Version++;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
            await using var verify = new OwnDayDbContext(options);
            Assert.Equal(InboxDraftStep.Text, (await verify.TelegramInboxDrafts.SingleAsync()).Step);
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }
}
