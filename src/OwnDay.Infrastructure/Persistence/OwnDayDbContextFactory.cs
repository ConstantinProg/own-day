using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OwnDay.Infrastructure.Persistence;

public sealed class OwnDayDbContextFactory : IDesignTimeDbContextFactory<OwnDayDbContext>
{
    public OwnDayDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default") ??
            "Host=localhost;Database=ownday;Username=ownday;Password=ownday";

        var options = new DbContextOptionsBuilder<OwnDayDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new OwnDayDbContext(options);
    }
}
