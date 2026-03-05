using System.Diagnostics.Metrics;
using Npgsql;

namespace Circles.Pathfinder.Host;

public sealed class LogStatsService : BackgroundService
{
    private readonly ILogger<LogStatsService> _log;
    private readonly NpgsqlDataSource _dataSource;
    private readonly PeriodicTimer _timer = new(Constants.StatsInterval);

    // Pool state tracked via MeterListener on Npgsql's System.Diagnostics.Metrics
    private int _idleConnections;
    private int _usedConnections;
    private MeterListener? _meterListener;

    public LogStatsService(ILogger<LogStatsService> log, NpgsqlDataSource dataSource)
    {
        _log = log;
        _dataSource = dataSource;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Set max pool gauge from connection string
        var csb = new NpgsqlConnectionStringBuilder(_dataSource.ConnectionString);
        GraphUpdateMetrics.DbPoolMaxConnections.Set(csb.MaxPoolSize);

        // Listen for Npgsql pool metrics via System.Diagnostics.Metrics
        _meterListener = new MeterListener();
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Npgsql" && instrument.Name == "db.client.connections.usage")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meterListener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) =>
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
        _meterListener.Start();

        try
        {
            while (await _timer.WaitForNextTickAsync(stop))
            {
                // Update pool Prometheus gauges
                var idle = Volatile.Read(ref _idleConnections);
                var used = Volatile.Read(ref _usedConnections);
                GraphUpdateMetrics.DbPoolIdleConnections.Set(idle);
                GraphUpdateMetrics.DbPoolBusyConnections.Set(used);
                GraphUpdateMetrics.DbPoolTotalConnections.Set(idle + used);

                // Existing latency stats
                var (cnt, avg, p95) = LatencyStats.Snapshot();
                if (cnt == 0)
                    continue;

                _log.LogInformation(
                    "Stats roll-up – requests={Count} avg={Avg:n1} ms p95={P95:n1} ms | pool: idle={PoolIdle} used={PoolUsed}",
                    cnt, avg, p95, idle, used);
            }
        }
        finally
        {
            _meterListener?.Dispose();
        }
    }
}
