using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host.State;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Circles.Pathfinder.Host;

public sealed class GraphReadinessHealthCheck(NetworkState state, CapacityGraphPool pool) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        var balanceGraphLoaded = state.BalanceGraph is not null;
        var trustLoaded = state.AccountTrusts.Count > 0;
        var poolReady = pool.HasCurrentSnapshot;

        var ready = balanceGraphLoaded && trustLoaded && poolReady;
        if (ready)
        {
            return Task.FromResult(
                HealthCheckResult.Healthy("Graphs are loaded and ready."));
        }

        var reason = !balanceGraphLoaded
            ? "Balance graph not loaded yet."
            : !trustLoaded
                ? "Trust graph not loaded yet."
                : "Capacity graph pool not initialized yet.";

        return Task.FromResult(HealthCheckResult.Unhealthy(reason));
    }
}
