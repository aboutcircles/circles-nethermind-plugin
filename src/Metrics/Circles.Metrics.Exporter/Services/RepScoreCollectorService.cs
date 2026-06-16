using System.Diagnostics;

namespace Circles.Metrics.Exporter.Services;

public class RepScoreCollectorService : BackgroundService
{
    private readonly RepScoreRepository _repository;
    private readonly ILogger<RepScoreCollectorService> _logger;
    private readonly TimeSpan _collectionInterval;

    public RepScoreCollectorService(
        RepScoreRepository repository,
        ILogger<RepScoreCollectorService> logger,
        IConfiguration configuration)
    {
        _repository = repository;
        _logger = logger;
        var intervalSeconds = configuration.GetValue<int>("RepScore:CollectionIntervalSeconds", 120);
        _collectionInterval = TimeSpan.FromSeconds(intervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RepScore Collector starting with {Interval}s interval", _collectionInterval.TotalSeconds);

        // Offset from other collectors to avoid query pileup at startup
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                await Task.WhenAll(
                    CollectBlacklistAsync(stoppingToken),
                    CollectDistributionAsync(stoppingToken),
                    CollectAnomaliesAsync(stoppingToken),
                    CollectTransferSharesAsync(stoppingToken));

                sw.Stop();
                RepScoreMetrics.CollectionDuration.Inc(sw.Elapsed.TotalSeconds);
                RepScoreMetrics.LastCollectionTimestamp.Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                _logger.LogDebug("RepScore metrics collection completed in {Duration}ms", sw.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during rep score metrics collection");
            }

            await Task.Delay(_collectionInterval, stoppingToken);
        }
    }

    private async Task CollectBlacklistAsync(CancellationToken ct)
    {
        try
        {
            var stats = await _repository.GetBlacklistStatsAsync(ct);

            RepScoreMetrics.BlacklistTotal.Set(stats.Total);
            RepScoreMetrics.BlacklistMembersTotal.Set(stats.MembersTotal);
            RepScoreMetrics.BlacklistMembersNonzeroScore.Set(stats.MembersNonzeroScore);
            RepScoreMetrics.BlacklistAdditions24h.Set(stats.Additions24h);
            RepScoreMetrics.BlacklistRemovals24h.Set(stats.Removals24h);
            RepScoreMetrics.BlacklistLastRefreshAgeSeconds.Set(stats.LastRefreshAgeSeconds);

            if (stats.MembersNonzeroScore > 0)
                _logger.LogWarning("Blacklisted ScoreGroup members with nonzero score: {Count}", stats.MembersNonzeroScore);
            else if (stats.MembersTotal > 0)
                _logger.LogInformation("Blacklisted ScoreGroup members present but all scored 0 (grace period ok): {Count}", stats.MembersTotal);

            _logger.LogDebug("Blacklist: total={Total}, members={Members}, age={Age}s",
                stats.Total, stats.MembersTotal, (int)stats.LastRefreshAgeSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect blacklist metrics");
            RepScoreMetrics.CollectionErrors.WithLabels("blacklist").Inc();
        }
    }

    private async Task CollectDistributionAsync(CancellationToken ct)
    {
        try
        {
            var dist = await _repository.GetScoreDistributionAsync(ct);

            RepScoreMetrics.MemberCount.Set(dist.MemberCount);
            RepScoreMetrics.ScoreAvg.Set(dist.Avg);
            RepScoreMetrics.ScoreMedian.Set(dist.Median);
            RepScoreMetrics.ScoreStdDev.Set(dist.StdDev);
            RepScoreMetrics.ZeroScoreCount.Set(dist.ZeroCount);
            RepScoreMetrics.HighScoreShare.Set(dist.HighShare);
            RepScoreMetrics.LastRefreshAgeSeconds.Set(dist.LastRefreshAgeSeconds);

            RepScoreMetrics.ScorePercentile.WithLabels("p25").Set(dist.P25);
            RepScoreMetrics.ScorePercentile.WithLabels("p50").Set(dist.Median);
            RepScoreMetrics.ScorePercentile.WithLabels("p75").Set(dist.P75);
            RepScoreMetrics.ScorePercentile.WithLabels("p90").Set(dist.P90);

            var buckets = new (string Label, long Count)[]
            {
                ("0-10",  dist.Bucket0_10),
                ("10-20", dist.Bucket10_20),
                ("20-30", dist.Bucket20_30),
                ("30-40", dist.Bucket30_40),
                ("40-50", dist.Bucket40_50),
                ("50-60", dist.Bucket50_60),
                ("60-70", dist.Bucket60_70),
                ("70-80", dist.Bucket70_80),
                ("80-90", dist.Bucket80_90),
                ("90-100", dist.Bucket90_100),
            };
            foreach (var (label, count) in buckets)
                RepScoreMetrics.ScoreBucketCount.WithLabels(label).Set(count);

            _logger.LogDebug("RepScore distribution: members={Count}, avg={Avg:F1}, high_share={Share:P1}, refresh_age={Age}s",
                dist.MemberCount, dist.Avg, dist.HighShare, (int)dist.LastRefreshAgeSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect rep score distribution metrics");
            RepScoreMetrics.CollectionErrors.WithLabels("distribution").Inc();
        }
    }

    private async Task CollectAnomaliesAsync(CancellationToken ct)
    {
        try
        {
            var anomaly = await _repository.GetAnomalyStatsAsync(ct);

            RepScoreMetrics.ScoreDrops24h.Set(anomaly.Drops24h);
            // Set all four tier×cause children every cycle (even at 0) so the
            // {tier="significant"} series the alert queries always exists.
            RepScoreMetrics.NewZeroScore24h.WithLabels("significant", "blacklist").Set(anomaly.NewZeroSignificantBlacklist24h);
            RepScoreMetrics.NewZeroScore24h.WithLabels("significant", "trust_collapse").Set(anomaly.NewZeroSignificantTrust24h);
            RepScoreMetrics.NewZeroScore24h.WithLabels("fringe", "blacklist").Set(anomaly.NewZeroFringeBlacklist24h);
            RepScoreMetrics.NewZeroScore24h.WithLabels("fringe", "trust_collapse").Set(anomaly.NewZeroFringeTrust24h);
            RepScoreMetrics.NewMembers24h.Set(anomaly.NewMembers24h);
            RepScoreMetrics.LostMembers24h.Set(anomaly.LostMembers24h);

            var newZeroSignificant = anomaly.NewZeroSignificantBlacklist24h + anomaly.NewZeroSignificantTrust24h;
            if (newZeroSignificant > 0)
                _logger.LogWarning(
                    "{Count} member(s) hit a significant rep_score=0 in 24h (blacklist={Blacklist}, trust_collapse={Trust}; prev_score >= {Threshold}/100)",
                    newZeroSignificant, anomaly.NewZeroSignificantBlacklist24h, anomaly.NewZeroSignificantTrust24h, _repository.NewZeroSignificantThreshold);

            if (anomaly.Drops24h > 0)
                _logger.LogInformation("Rep score drops (>={Threshold}pts) in 24h: {Count}", _repository.ScoreDropThreshold, anomaly.Drops24h);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect rep score anomaly metrics");
            RepScoreMetrics.CollectionErrors.WithLabels("anomalies").Inc();
        }
    }

    private async Task CollectTransferSharesAsync(CancellationToken ct)
    {
        try
        {
            var shares = await _repository.GetTransferSharesAsync(ct);

            RepScoreMetrics.HighRepTransferShare.WithLabels("24h").Set(shares.HighRepShare24h);
            RepScoreMetrics.ZeroRepTransferShare.WithLabels("24h").Set(shares.ZeroRepShare24h);
            RepScoreMetrics.HighRepTransferShare.WithLabels("7d").Set(shares.HighRepShare7d);
            RepScoreMetrics.ZeroRepTransferShare.WithLabels("7d").Set(shares.ZeroRepShare7d);

            _logger.LogDebug("Transfer shares: high_rep_24h={H24:P1}, zero_rep_24h={Z24:P1}",
                shares.HighRepShare24h, shares.ZeroRepShare24h);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect transfer share metrics");
            RepScoreMetrics.CollectionErrors.WithLabels("transfer_shares").Inc();
        }
    }
}
