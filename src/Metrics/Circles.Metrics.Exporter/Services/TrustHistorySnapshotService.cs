namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Background service that creates daily snapshots of trust scores for historical analysis.
/// Enables anomaly detection metrics (score drops, spikes) by comparing current vs historical scores.
/// Runs once per day at a configurable hour (default: 4 AM UTC).
/// </summary>
public class TrustHistorySnapshotService : BackgroundService
{
    private readonly TrustRepository _repository;
    private readonly ILogger<TrustHistorySnapshotService> _logger;
    private readonly int _snapshotHourUtc;
    private readonly int _retentionDays;

    public TrustHistorySnapshotService(
        TrustRepository repository,
        ILogger<TrustHistorySnapshotService> logger,
        IConfiguration configuration)
    {
        _repository = repository;
        _logger = logger;

        // Hour of day (UTC) to run snapshot (default: 4 AM)
        _snapshotHourUtc = configuration.GetValue<int>("Metrics:HistorySnapshotHourUtc", 4);

        // Days to retain history (default: 90 days)
        _retentionDays = configuration.GetValue<int>("Metrics:HistoryRetentionDays", 90);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Trust History Snapshot Service starting. Snapshot hour: {Hour}:00 UTC, Retention: {Days} days",
            _snapshotHourUtc, _retentionDays);

        // Initial delay to let the application start up
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        // Take an initial snapshot on startup if none exists today
        await TryCreateSnapshotAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var nextRun = GetNextSnapshotTime(now);
            var delay = nextRun - now;

            _logger.LogDebug("Next history snapshot scheduled for {NextRun} (in {Hours:F1} hours)",
                nextRun, delay.TotalHours);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await TryCreateSnapshotAsync(stoppingToken);
        }
    }

    private DateTime GetNextSnapshotTime(DateTime now)
    {
        // Calculate next occurrence of the snapshot hour
        var today = now.Date.AddHours(_snapshotHourUtc);

        if (now < today)
        {
            return today;
        }

        // Already past today's snapshot time, schedule for tomorrow
        return today.AddDays(1);
    }

    private async Task TryCreateSnapshotAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Starting trust score history snapshot...");

            var recordsCreated = await _repository.CreateHistorySnapshotAsync(ct);

            if (recordsCreated > 0)
            {
                _logger.LogInformation("Trust score history snapshot completed: {Records} records", recordsCreated);

                // Cleanup old records after successful snapshot
                var recordsDeleted = await _repository.CleanupOldHistoryAsync(_retentionDays, ct);
                if (recordsDeleted > 0)
                {
                    _logger.LogInformation("Cleaned up {Records} old history records", recordsDeleted);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to create trust score history snapshot");
        }
    }
}
