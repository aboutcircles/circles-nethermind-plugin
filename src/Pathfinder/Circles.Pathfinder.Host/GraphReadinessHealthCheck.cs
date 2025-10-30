using Circles.Pathfinder.Host.State;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Circles.Pathfinder.Host;

public sealed class GraphReadinessHealthCheck(NetworkState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        var balanceGraphLoaded = state.BalanceGraph is not null;
        var trustLoaded = state.AccountTrusts.Count > 0;

        var ready = balanceGraphLoaded && trustLoaded;
        if (ready)
        {
            return Task.FromResult(
                HealthCheckResult.Healthy("Graphs are loaded and ready."));
        }

        var reason = !balanceGraphLoaded
            ? "Balance graph not loaded yet."
            : "Trust graph not loaded yet.";

        return Task.FromResult(HealthCheckResult.Unhealthy(reason));
    }
}