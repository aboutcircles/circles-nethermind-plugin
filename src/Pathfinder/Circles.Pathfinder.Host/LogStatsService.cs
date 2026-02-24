namespace Circles.Pathfinder.Host;

public sealed class LogStatsService : BackgroundService
{
    private readonly ILogger<LogStatsService> _log;
    private readonly PeriodicTimer _timer = new(Constants.StatsInterval);

    public LogStatsService(ILogger<LogStatsService> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (await _timer.WaitForNextTickAsync(stop))
        {
            var (cnt, avg, p95) = LatencyStats.Snapshot();
            if (cnt == 0)
            {
                continue;
            }

            _log.LogInformation(
                "Stats roll-up – requests={Count} avg={Avg:n1} ms p95={P95:n1} ms",
                cnt, avg, p95);
        }
    }
}