using System.Diagnostics.Metrics;
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
    private readonly CacheServiceState _state;
    private readonly CacheContainer _caches;
    private readonly NpgsqlDataSource _readonlyDataSource;
    private readonly TimeSpan _updateInterval = TimeSpan.FromSeconds(10);

    // Pool state tracked via MeterListener on Npgsql's System.Diagnostics.Metrics
    private int _idleConnections;
    private int _usedConnections;

    public MetricsUpdateService(
        ILogger<MetricsUpdateService> logger,
        CacheServiceState state,
        CacheContainer caches,
        NpgsqlDataSource readonlyDataSource)
    {
        _logger = logger;
        _state = state;
        _caches = caches;
        _readonlyDataSource = readonlyDataSource;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Metrics update service started");

        // Set max pool gauge from connection string
        var csb = new NpgsqlConnectionStringBuilder(_readonlyDataSource.ConnectionString);
        CacheMetrics.DbPoolMaxConnections.Set(csb.MaxPoolSize);

        // Listen for Npgsql pool metrics via System.Diagnostics.Metrics
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Npgsql" && instrument.Name == "db.client.connections.usage")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "state")
                {
                    if (tag.Value?.ToString() == "idle")
                        Interlocked.Exchange(ref _idleConnections, measurement);
                    else if (tag.Value?.ToString() == "used")
                        Interlocked.Exchange(ref _usedConnections, measurement);
                }
            }
        });
        meterListener.Start();

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

        // Update pool metrics
        var idle = Volatile.Read(ref _idleConnections);
        var used = Volatile.Read(ref _usedConnections);
        CacheMetrics.DbPoolIdleConnections.Set(idle);
        CacheMetrics.DbPoolBusyConnections.Set(used);
        CacheMetrics.DbPoolTotalConnections.Set(idle + used);

        // Get database head to calculate lag
        try
        {
            await using var conn = await _readonlyDataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT COALESCE(MAX(\"blockNumber\"), 0) FROM \"System_Block\"", conn);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result != null && result != DBNull.Value)
            {
                // blockNumber is BIGINT, can be returned as int or long depending on value
                var dbHead = result switch
                {
                    long l => l,
                    int i => i,
                    _ => Convert.ToInt64(result)
                };
                // Only publish meaningful lag when warmup is complete. During warmup,
                // LastProcessedBlock is -1 (sentinel), making lag = dbHead + 1 (~45M blocks).
                // Publishing that would trigger IndexerLagCritical/CacheDatabaseLagCritical alerts
                // on every re-warmup cycle.
                var lag = _state.WarmupComplete ? _state.GetLag(dbHead) : 0;
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
