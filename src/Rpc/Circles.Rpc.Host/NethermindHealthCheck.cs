 using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Health check for Nethermind connectivity.
/// </summary>
public class NethermindConnectionHealthCheck : IHealthCheck
{
    private readonly NethermindRpcClient _rpcClient;

    public NethermindConnectionHealthCheck(NethermindRpcClient rpcClient)
    {
        _rpcClient = rpcClient;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _rpcClient.IsSynced();
            return HealthCheckResult.Healthy("Nethermind connection is possible");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cannot connect to Nethermind", ex);
        }
    }
}

/// <summary>
/// Health check for Nethermind sync status.
/// </summary>
public class NethermindSyncHealthCheck : IHealthCheck
{
    private readonly NethermindRpcClient _rpcClient;

    public NethermindSyncHealthCheck(NethermindRpcClient rpcClient)
    {
        _rpcClient = rpcClient;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var isSynced = await _rpcClient.IsSynced();

            if (isSynced)
            {
                return HealthCheckResult.Healthy("Nethermind is fully synced");
            }
            else
            {
                return HealthCheckResult.Unhealthy("Nethermind is still syncing");
            }
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cannot connect to Nethermind", ex);
        }
    }
}

/// <summary>
/// Health check for Pathfinder connectivity.
/// </summary>
public class PathfinderConnectionHealthCheck : IHealthCheck
{
    private readonly Settings _settings;
    private readonly IHttpClientFactory _httpClientFactory;

    public PathfinderConnectionHealthCheck(Settings settings, IHttpClientFactory httpClientFactory)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // If pathfinder URL is not configured, consider it healthy (optional dependency)
        if (string.IsNullOrEmpty(_settings.ExternalPathfinderUrl))
        {
            return HealthCheckResult.Degraded("Pathfinder URL not configured");
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            var baseUrl = _settings.ExternalPathfinderUrl.TrimEnd('/');
            // Check the /ready endpoint which waits for graphs to be fully loaded
            var readyUrl = $"{baseUrl}/ready";

            var response = await client.GetAsync(readyUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy($"Pathfinder is ready at {_settings.ExternalPathfinderUrl}");
            }
            else
            {
                // If the Pathfinder is not ready, provide a detailed status
                var reason = response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                    ? "Pathfinder graphs are still loading"
                    : $"Pathfinder returned status {response.StatusCode}";
                return HealthCheckResult.Unhealthy(reason);
            }
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded($"Cannot connect to Pathfinder at {_settings.ExternalPathfinderUrl}", ex);
        }
    }
}

/// <summary>
/// Health check for database connectivity.
/// </summary>
public class DatabaseConnectionHealthCheck : IHealthCheck
{
    private readonly Settings _settings;

    public DatabaseConnectionHealthCheck(Settings settings)
    {
        _settings = settings;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_settings.IndexReadonlyDbConnectionString);
            await connection.OpenAsync(cancellationToken);

            // Optionally, execute a simple query to verify the connection is fully functional
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy("Database connection is healthy");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cannot connect to database", ex);
        }
    }
}