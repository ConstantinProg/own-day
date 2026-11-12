using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OwnDay.Application.Learning.Requests;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Learning;

namespace OwnDay.Host.HealthChecks;

public sealed class LearningQueueHealthCheck(IServiceScopeFactory scopes, IOptions<LearningRequestProcessingOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var pending = db.Set<LearningRequest>().Where(request => request.Status != LearningRequestStatus.Completed);
        var count = await pending.LongCountAsync(cancellationToken);
        if (!options.Value.Enabled)
            return count == 0 ? HealthCheckResult.Healthy("Learning worker disabled; durable ingress must remain closed.")
                : HealthCheckResult.Unhealthy("Accepted Learning work requires an enabled compatible worker.",
                    data: new Dictionary<string, object> { ["pending"] = count });
        var oldest = await pending.MinAsync(request => (DateTime?)request.ReceivedAt, cancellationToken);
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
        var age = oldest is null ? 0 : Math.Max(0, (now - oldest.Value).TotalSeconds);
        var data = new Dictionary<string, object> { ["pending"] = count, ["oldest_age_seconds"] = age };
        return age >= options.Value.MaxQueueAgeSeconds
            ? HealthCheckResult.Unhealthy("Learning backlog requires recovery; new acceptance is closed.", data: data)
            : HealthCheckResult.Healthy(options.Value.AcceptancePaused ? "Learning acceptance paused; worker draining." : "Learning queue available.", data);
    }
}
