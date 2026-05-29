using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Circles.Rpc.Host.Endpoints;

/// <summary>
/// Health-probe endpoints: /live (liveness), /ready (composite readiness), /health (nethermind connectivity).
/// </summary>
public static class HealthCheckEndpoints
{
    public static WebApplication MapCirclesHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/live", new HealthCheckOptions
        {
            Predicate = hc => hc.Tags.Contains("live")
        });

        // readiness: nethermind sync status + pathfinder connectivity + database connectivity + indexer sync
        // Degraded returns 200 today. Flipping to 503 (so load balancers drain a partially-broken node)
        // requires the surrounding probe + DNS-drain layer to also accept 503 — out of scope here.
        app.MapHealthChecks("/ready", new HealthCheckOptions
        {
            Predicate = hc => hc.Tags.Contains("nethermind-sync") || hc.Tags.Contains("pathfinder-connection") || hc.Tags.Contains("database-connection") || hc.Tags.Contains("indexer-sync"),
            AllowCachingResponses = false,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Degraded] = StatusCodes.Status200OK
            }
        });

        // nethermind connectivity
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = hc => hc.Tags.Contains("nethermind-connection"),
            AllowCachingResponses = false,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Degraded] = StatusCodes.Status200OK
            }
        });

        return app;
    }
}
