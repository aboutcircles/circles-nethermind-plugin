using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Circles.Pathfinder.Host;

/// <summary>
/// Probes the database with SELECT 1 to verify connectivity.
/// Registered on the "ready" tag so /ready fails when the DB is unreachable.
/// </summary>
public sealed class DbConnectivityHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<DbConnectivityHealthCheck> _log;

    public DbConnectivityHealthCheck(NpgsqlDataSource dataSource, ILogger<DbConnectivityHealthCheck> log)
    {
        _dataSource = dataSource;
        _log = log;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            cmd.CommandTimeout = 5;
            await cmd.ExecuteScalarAsync(ct);

            return HealthCheckResult.Healthy("Database is reachable.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Database health check failed");
            return HealthCheckResult.Unhealthy("Database is unreachable.", ex);
        }
    }
}
