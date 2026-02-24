using Circles.Pathfinder.Host.State;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Circles.Pathfinder.Host;

/// <summary>
/// Health check that monitors the health of background services, specifically
/// the NetworkStateUpdaterService which is critical for keeping graph data fresh.
/// </summary>
public sealed class BackgroundServiceHealthCheck : IHealthCheck
{
    private readonly NetworkState _networkState;
    private readonly ILogger<BackgroundServiceHealthCheck> _log;

    // Consider service unhealthy if no updates for more than 5 minutes
    private static readonly TimeSpan MaxStaleDuration = TimeSpan.FromMinutes(5);

    public BackgroundServiceHealthCheck(NetworkState networkState, ILogger<BackgroundServiceHealthCheck> log)
    {
        _networkState = networkState;
        _log = log;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var lastUpdateTime = _networkState.LastUpdateTime;
        var currentTime = DateTime.UtcNow;

        if (lastUpdateTime == default)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "NetworkStateUpdaterService has not completed any updates yet."));
        }

        var timeSinceLastUpdate = currentTime - lastUpdateTime;

        if (timeSinceLastUpdate > MaxStaleDuration)
        {
            var errorMessage = $"NetworkStateUpdaterService data is stale (last update: {lastUpdateTime:u}, age: {timeSinceLastUpdate:hh\\:mm\\:ss}). " +
                              "Background service may have failed or network connectivity issues.";

            _log.LogError("Health check UNHEALTHY: {ErrorMessage}", errorMessage);

            // Return Unhealthy (not Degraded) so load balancers remove this instance
            return Task.FromResult(HealthCheckResult.Unhealthy(
                errorMessage,
                data: new Dictionary<string, object>
                {
                    ["LastUpdateTime"] = lastUpdateTime,
                    ["TimeSinceLastUpdate"] = timeSinceLastUpdate.ToString(@"hh\:mm\:ss"),
                    ["MaxStaleDuration"] = MaxStaleDuration.ToString(@"hh\:mm\:ss")
                }));
        }

        var successMessage = $"Background services are healthy (last update: {lastUpdateTime:u}, age: {timeSinceLastUpdate:hh\\:mm\\:ss})";

        return Task.FromResult(HealthCheckResult.Healthy(
            successMessage,
            data: new Dictionary<string, object>
            {
                ["LastUpdateTime"] = lastUpdateTime,
                ["TimeSinceLastUpdate"] = timeSinceLastUpdate.ToString(@"hh\:mm\:ss"),
                ["MaxStaleDuration"] = MaxStaleDuration.ToString(@"hh\:mm\:ss")
            }));
    }
}