using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Circles.Index;

public class PluginHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        bool isHealthy = true;

        if (isHealthy)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Plugin is healthy"));
        }

        return Task.FromResult(HealthCheckResult.Unhealthy("Plugin is unhealthy"));
    }
}