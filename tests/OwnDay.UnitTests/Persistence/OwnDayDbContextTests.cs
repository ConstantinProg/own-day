using Microsoft.EntityFrameworkCore;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.UnitTests.Persistence;

public sealed class OwnDayDbContextTests
{
    [Fact]
    public void Model_CurrentNpgsqlModel_MatchesMigrationSnapshot()
    {
        using var context = new OwnDayDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
