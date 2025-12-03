using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Metrics;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Background service that periodically updates Prometheus metrics.
/// </summary>
public class MetricsUpdateService : BackgroundService
{
    private readonly ILogger<MetricsUpdateService> _logger;
    private readonly CacheServiceSettings _settings;
    private readonly CacheServiceState _state;
    private readonly CacheContainer _caches;
    private readonly TimeSpan _updateInterval = TimeSpan.FromSeconds(10);

    public MetricsUpdateService(
        ILogger<MetricsUpdateService> logger,
        CacheServiceSettings settings,
        CacheServiceState state,
        CacheContainer caches)
    {
        _logger = logger;
        _settings = settings;
        _state = state;
        _caches = caches;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Metrics update service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UpdateMetricsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating metrics");
            }

            await Task.Delay(_updateInterval, stoppingToken);
        }
    }

    private async Task UpdateMetricsAsync(CancellationToken ct)
    {
        // Update state metrics
        CacheMetrics.LastProcessedBlock.Set(_state.LastProcessedBlock);
        CacheMetrics.WarmupComplete.Set(_state.WarmupComplete ? 1 : 0);
        CacheMetrics.ListenerConnected.Set(_state.ListenerConnected ? 1 : 0);

        // Get database head to calculate lag
        try
        {
            await using var conn = new NpgsqlConnection(_settings.EffectiveReadonlyConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT COALESCE(MAX(\"blockNumber\"), 0) FROM \"System_Block\"", conn);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result != null && result != DBNull.Value)
            {
                var dbHead = Convert.ToInt64(result);
                var lag = _state.GetLag(dbHead);
                CacheMetrics.DatabaseLag.Set(lag);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query database head for metrics");
        }

        // Update cache size metrics
        var stats = _caches.GetStatistics();

        CacheMetrics.V1Avatars.Set(GetLongValue(stats, "v1_avatars"));
        CacheMetrics.V2Avatars.Set(GetLongValue(stats, "v2_avatars"));
        CacheMetrics.Groups.Set(GetLongValue(stats, "groups"));
        CacheMetrics.V1Balances.Set(GetLongValue(stats, "v1_balances"));
        CacheMetrics.V2Balances.Set(GetLongValue(stats, "v2_balances"));
        CacheMetrics.IndexedAddressesV1.Set(GetLongValue(stats, "v1_indexed_addresses"));
        CacheMetrics.IndexedAddressesV2.Set(GetLongValue(stats, "v2_indexed_addresses"));
        CacheMetrics.TotalCacheEntries.Set(GetLongValue(stats, "total_entries"));
    }

    private static long GetLongValue(Dictionary<string, object> stats, string key)
    {
        if (stats.TryGetValue(key, out var value))
        {
            return value switch
            {
                long l => l,
                int i => i,
                _ => 0
            };
        }
        return 0;
    }
}
