using System.Diagnostics;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Background service that periodically collects trust score metrics and updates Prometheus gauges.
/// Queries pre-computed trust scores from the analytics database (trust_scores_current table).
/// Runs on 300-second interval since trust scores are computed periodically, not in real-time.
/// </summary>
public class TrustCollectorService : BackgroundService
{
    private readonly TrustRepository _repository;
    private readonly ILogger<TrustCollectorService> _logger;
    private readonly TimeSpan _collectionInterval;

    // Time windows for metrics
    private static readonly TimeSpan Window24H = TimeSpan.FromHours(24);
    private static readonly TimeSpan Window7D = TimeSpan.FromDays(7);
    private static readonly TimeSpan Window30D = TimeSpan.FromDays(30);

    public TrustCollectorService(
        TrustRepository repository,
        ILogger<TrustCollectorService> logger,
        IConfiguration configuration)
    {
        _repository = repository;
        _logger = logger;

        // Trust metrics collection interval (default: 5 minutes / 300 seconds)
        var intervalSeconds = configuration.GetValue<int>("Metrics:TrustCollectionIntervalSeconds", 300);
        _collectionInterval = TimeSpan.FromSeconds(intervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Trust Collector starting with {Interval}s interval",
            _collectionInterval.TotalSeconds);

        // Wait for app startup and database to be ready
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                await CollectAllTrustMetricsAsync(stoppingToken);

                sw.Stop();
                TrustMetrics.CollectionDuration.Inc(sw.Elapsed.TotalSeconds);
                TrustMetrics.LastCollectionTimestamp.Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                _logger.LogDebug("Trust metrics collection completed in {Duration}ms", sw.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during trust metrics collection");
            }

            await Task.Delay(_collectionInterval, stoppingToken);
        }
    }

    private async Task CollectAllTrustMetricsAsync(CancellationToken ct)
    {
        // Run independent collections in parallel
        await Task.WhenAll(
            CollectScoreDistributionAsync(ct),
            CollectTrustLevelDistributionAsync(ct),
            CollectConfidenceMetricsAsync(ct),
            CollectNetworkHealthMetricsAsync(ct),
            CollectAnomalyDetectionMetricsAsync(ct),
            CollectScoreBucketsAsync(ct),
            CollectEconomicCorrelationMetricsAsync(ct),
            CollectTimestampMetricsAsync(ct)
        );
    }

    /// <summary>
    /// Collects score distribution metrics (avg, median, stddev, percentiles).
    /// </summary>
    private async Task CollectScoreDistributionAsync(CancellationToken ct)
    {
        try
        {
            var dist = await _repository.GetScoreDistributionAsync(ct);

            TrustMetrics.ScoreAvg.Set(dist.Avg);
            TrustMetrics.ScoreMedian.Set(dist.Median);
            TrustMetrics.ScoreStdDev.Set(dist.StdDev);
            TrustMetrics.ScoreMin.Set(dist.Min);
            TrustMetrics.ScoreMax.Set(dist.Max);
            TrustMetrics.TotalScoredAccounts.Set(dist.TotalCount);

            // Percentiles
            TrustMetrics.ScorePercentile.WithLabels("p50").Set(dist.Median);
            TrustMetrics.ScorePercentile.WithLabels("p75").Set(dist.P75);
            TrustMetrics.ScorePercentile.WithLabels("p90").Set(dist.P90);
            TrustMetrics.ScorePercentile.WithLabels("p99").Set(dist.P99);

            _logger.LogDebug("Trust score distribution: avg={Avg:F1}, median={Median:F1}, stddev={StdDev:F2}, count={Count}",
                dist.Avg, dist.Median, dist.StdDev, dist.TotalCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect trust score distribution metrics");
            TrustMetrics.CollectionErrors.WithLabels("score_distribution").Inc();
        }
    }

    /// <summary>
    /// Collects trust level distribution (count and percentage at each level).
    /// </summary>
    private async Task CollectTrustLevelDistributionAsync(CancellationToken ct)
    {
        try
        {
            var levelCounts = await _repository.GetTrustLevelCountsAsync(ct);
            var totalCount = levelCounts.Values.Sum();

            foreach (var (level, count) in levelCounts)
            {
                TrustMetrics.LevelCount.WithLabels(level).Set(count);

                var percentage = totalCount > 0 ? (count * 100.0 / totalCount) : 0;
                TrustMetrics.LevelPercentage.WithLabels(level).Set(percentage);
            }

            _logger.LogDebug("Trust level distribution collected: {Levels}",
                string.Join(", ", levelCounts.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect trust level distribution metrics");
            TrustMetrics.CollectionErrors.WithLabels("level_distribution").Inc();
        }
    }

    /// <summary>
    /// Collects confidence metrics (avg, median, low confidence count).
    /// </summary>
    private async Task CollectConfidenceMetricsAsync(CancellationToken ct)
    {
        try
        {
            var conf = await _repository.GetConfidenceMetricsAsync(ct);

            TrustMetrics.ConfidenceAvg.Set(conf.Avg);
            TrustMetrics.ConfidenceMedian.Set(conf.Median);
            TrustMetrics.LowConfidenceCount.Set(conf.LowConfidenceCount);

            _logger.LogDebug("Trust confidence metrics: avg={Avg:F2}, median={Median:F2}, lowConf={LowCount}",
                conf.Avg, conf.Median, conf.LowConfidenceCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect trust confidence metrics");
            TrustMetrics.CollectionErrors.WithLabels("confidence").Inc();
        }
    }

    /// <summary>
    /// Collects network health metrics (velocity, churn, reciprocity, density, degrees).
    /// </summary>
    private async Task CollectNetworkHealthMetricsAsync(CancellationToken ct)
    {
        // Trust velocity and churn by window
        try
        {
            var velocity24h = await _repository.GetTrustVelocityAsync(Window24H, ct);
            var velocity7d = await _repository.GetTrustVelocityAsync(Window7D, ct);
            var velocity30d = await _repository.GetTrustVelocityAsync(Window30D, ct);

            TrustMetrics.TrustVelocity.WithLabels("24h").Set(velocity24h);
            TrustMetrics.TrustVelocity.WithLabels("7d").Set(velocity7d);
            TrustMetrics.TrustVelocity.WithLabels("30d").Set(velocity30d);

            var churn24h = await _repository.GetTrustChurnAsync(Window24H, ct);
            var churn7d = await _repository.GetTrustChurnAsync(Window7D, ct);
            var churn30d = await _repository.GetTrustChurnAsync(Window30D, ct);

            TrustMetrics.TrustChurn.WithLabels("24h").Set(churn24h);
            TrustMetrics.TrustChurn.WithLabels("7d").Set(churn7d);
            TrustMetrics.TrustChurn.WithLabels("30d").Set(churn30d);

            // Net change
            TrustMetrics.TrustNetChange.WithLabels("24h").Set(velocity24h - churn24h);
            TrustMetrics.TrustNetChange.WithLabels("7d").Set(velocity7d - churn7d);
            TrustMetrics.TrustNetChange.WithLabels("30d").Set(velocity30d - churn30d);

            _logger.LogDebug("Trust velocity: 24h={V24h}, 7d={V7d}, 30d={V30d}",
                velocity24h, velocity7d, velocity30d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect trust velocity/churn metrics");
            TrustMetrics.CollectionErrors.WithLabels("velocity_churn").Inc();
        }

        // Reciprocity rate
        try
        {
            var reciprocity = await _repository.GetTrustReciprocityRateAsync(ct);
            TrustMetrics.TrustReciprocityRate.Set(reciprocity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect trust reciprocity rate");
            TrustMetrics.CollectionErrors.WithLabels("reciprocity").Inc();
        }

        // Graph density
        try
        {
            var density = await _repository.GetTrustGraphDensityAsync(ct);
            TrustMetrics.TrustGraphDensity.Set(density);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect trust graph density");
            TrustMetrics.CollectionErrors.WithLabels("density").Inc();
        }

        // Average degrees
        try
        {
            var avgOut = await _repository.GetAvgOutDegreeAsync(ct);
            var avgIn = await _repository.GetAvgInDegreeAsync(ct);

            TrustMetrics.AvgOutDegree.Set(avgOut);
            TrustMetrics.AvgInDegree.Set(avgIn);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect trust degree metrics");
            TrustMetrics.CollectionErrors.WithLabels("degrees").Inc();
        }
    }

    /// <summary>
    /// Collects anomaly detection metrics (score drops, spikes, low trust new accounts).
    /// </summary>
    private async Task CollectAnomalyDetectionMetricsAsync(CancellationToken ct)
    {
        // Score drops
        try
        {
            var drops24h = await _repository.GetScoreDropsAsync(Window24H, 20, ct);
            var drops7d = await _repository.GetScoreDropsAsync(Window7D, 20, ct);

            TrustMetrics.ScoreDrops.WithLabels("24h").Set(drops24h);
            TrustMetrics.ScoreDrops.WithLabels("7d").Set(drops7d);

            if (drops24h > 0)
            {
                _logger.LogInformation("Detected {Count} significant trust score drops in last 24h", drops24h);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect score drops metrics");
            TrustMetrics.CollectionErrors.WithLabels("score_drops").Inc();
        }

        // Score spikes (suspicious increases)
        try
        {
            var spikes24h = await _repository.GetScoreSpikesAsync(Window24H, 30, ct);
            var spikes7d = await _repository.GetScoreSpikesAsync(Window7D, 30, ct);

            TrustMetrics.ScoreSpikes.WithLabels("24h").Set(spikes24h);
            TrustMetrics.ScoreSpikes.WithLabels("7d").Set(spikes7d);

            if (spikes24h > 10)
            {
                _logger.LogWarning("Detected {Count} suspicious trust score spikes in last 24h", spikes24h);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect score spikes metrics");
            TrustMetrics.CollectionErrors.WithLabels("score_spikes").Inc();
        }

        // Low trust new accounts
        try
        {
            var lowTrust24h = await _repository.GetLowTrustNewAccountsAsync(Window24H, ct);
            var lowTrust7d = await _repository.GetLowTrustNewAccountsAsync(Window7D, ct);

            TrustMetrics.LowTrustNewAccounts.WithLabels("24h").Set(lowTrust24h);
            TrustMetrics.LowTrustNewAccounts.WithLabels("7d").Set(lowTrust7d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect low trust new accounts metrics");
            TrustMetrics.CollectionErrors.WithLabels("low_trust_new").Inc();
        }

        // Penalized accounts
        try
        {
            var penalized = await _repository.GetPenalizedAccountsAsync(ct);
            TrustMetrics.PenalizedAccounts.Set(penalized);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect penalized accounts metrics");
            TrustMetrics.CollectionErrors.WithLabels("penalized").Inc();
        }
    }

    /// <summary>
    /// Collects score bucket distribution (histogram-like).
    /// </summary>
    private async Task CollectScoreBucketsAsync(CancellationToken ct)
    {
        try
        {
            var buckets = await _repository.GetScoreBucketsAsync(ct);

            foreach (var (bucket, count) in buckets)
            {
                TrustMetrics.ScoreBucketCount.WithLabels(bucket).Set(count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect score bucket metrics");
            TrustMetrics.CollectionErrors.WithLabels("score_buckets").Inc();
        }
    }

    /// <summary>
    /// Collects economic-trust correlation metrics (volume by trust level).
    /// </summary>
    private async Task CollectEconomicCorrelationMetricsAsync(CancellationToken ct)
    {
        foreach (var (window, label) in new[] { (Window24H, "24h"), (Window7D, "7d") })
        {
            try
            {
                var metrics = await _repository.GetTrustVolumeMetricsAsync(window, ct);

                TrustMetrics.HighTrustVolume.WithLabels(label).Set(metrics.HighTrustVolume);
                TrustMetrics.LowTrustVolume.WithLabels(label).Set(metrics.LowTrustVolume);
                TrustMetrics.TrustWeightedVolume.WithLabels(label).Set(metrics.TrustWeightedVolume);

                var highTrustShare = metrics.TotalVolume > 0
                    ? (metrics.HighTrustVolume / metrics.TotalVolume) * 100
                    : 0;
                TrustMetrics.HighTrustVolumeShare.WithLabels(label).Set(highTrustShare);

                _logger.LogDebug("Trust-volume correlation ({Window}): high={High:F0}, low={Low:F0}, share={Share:F1}%",
                    label, metrics.HighTrustVolume, metrics.LowTrustVolume, highTrustShare);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to collect trust volume metrics for window {Window}", label);
                TrustMetrics.CollectionErrors.WithLabels($"volume_{label}").Inc();
            }
        }
    }

    /// <summary>
    /// Collects timestamp metrics (last compute time, history count).
    /// </summary>
    private async Task CollectTimestampMetricsAsync(CancellationToken ct)
    {
        try
        {
            var lastCompute = await _repository.GetLastComputeTimeAsync(ct);
            if (lastCompute > DateTimeOffset.MinValue)
            {
                TrustMetrics.LastScoreComputeTime.Set(lastCompute.ToUnixTimeSeconds());
            }

            var historyCount = await _repository.GetHistoryCountAsync(ct);
            TrustMetrics.ScoredAccountsWithHistory.Set(historyCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect timestamp metrics");
            TrustMetrics.CollectionErrors.WithLabels("timestamps").Inc();
        }
    }
}
