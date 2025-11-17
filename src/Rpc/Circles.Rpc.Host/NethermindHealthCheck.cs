 using Microsoft.Extensions.Diagnostics.HealthChecks;

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