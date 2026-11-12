using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OwnDay.Host.HealthChecks;
using OwnDay.Infrastructure.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.App;

public sealed class LearningQueueHealthCheckTests
{
    [Theory]
    [InlineData(false, HealthStatus.Healthy)]
    [InlineData(true, HealthStatus.Unhealthy)]
    public async Task Check_NonPostgresProvider_DoesNotQueryUnsupportedQueue(bool enabled, HealthStatus expected)
    {
        // No queue schema or shared live connection exists on this unsupported provider.
        using var services = new ServiceCollection().AddDbContext<OwnDayDbContext>(options =>
            options.UseSqlite("Data Source=:memory:")).BuildServiceProvider();
        var check = new LearningQueueHealthCheck(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new LearningRequestProcessingOptions { Enabled = enabled }));
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(expected, result.Status);
    }
}
