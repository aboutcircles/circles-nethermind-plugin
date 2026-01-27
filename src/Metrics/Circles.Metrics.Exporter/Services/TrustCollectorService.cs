using System.Diagnostics;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Background service that periodically collects trust score metrics and updates Prometheus gauges.
/// Queries pre-computed trust scores from the analytics database (trust_scores_current table).
/// Runs on 300-second interval since trust scores are computed periodically, not in real-time.
///
/// Optimization: Uses batched queries to reduce database round-trips from ~25 to ~6 per cycle.
/// </summary>
public class TrustCollectorService : BackgroundService
{
    private readonly TrustRepository _repository;
    private readonly ILogger<TrustCollectorService> _logger;
    private readonly TimeSpan _collectionInterval;

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
        // Offset from KPI (5s) and Liquidity (60s) to prevent query pileup
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

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
        // Run batched collections in parallel (optimized: 25 queries → 6 queries)
        await Task.WhenAll(
            CollectScoreDistributionBatchedAsync(ct),    // Score dist + level counts + score buckets
            CollectConfidenceMetricsAsync(ct),          // Single query, different table
            CollectNetworkHealthBatchedAsync(ct),       // Velocity + churn + reciprocity + density + degrees
            CollectAnomalyDetectionBatchedAsync(ct),    // Drops + spikes + low trust new + penalized
            CollectEconomicCorrelationBatchedAsync(ct), // Volume metrics for both windows
            CollectTimestampMetricsAsync(ct)            // Small queries, different table
        );
    }

    /// <summary>
    /// Collects score distribution, level counts, and buckets in a single query.
    /// </summary>
    private async Task CollectScoreDistributionBatchedAsync(CancellationToken ct)
    {
        try
        {
            var dist = await _repository.GetScoreDistributionBatchedAsync(ct);

            // Score distribution stats
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

            // Trust level counts
            var levelCounts = new Dictionary<string, long>
            {
                ["VERY_HIGH"] = dist.LevelVeryHigh,
                ["HIGH"] = dist.LevelHigh,
                ["MEDIUM"] = dist.LevelMedium,
                ["LOW"] = dist.LevelLow,
                ["VERY_LOW"] = dist.LevelVeryLow,
                ["UNKNOWN"] = dist.LevelUnknown
            };
            var totalLevelCount = levelCounts.Values.Sum();

            foreach (var (level, count) in levelCounts)
            {
                TrustMetrics.LevelCount.WithLabels(level).Set(count);
                var percentage = totalLevelCount > 0 ? (count * 100.0 / totalLevelCount) : 0;
                TrustMetrics.LevelPercentage.WithLabels(level).Set(percentage);
            }

            // Score buckets
            var buckets = new Dictionary<string, long>
            {
                ["90-100"] = dist.Bucket90_100,
                ["80-90"] = dist.Bucket80_90,
                ["70-80"] = dist.Bucket70_80,
                ["60-70"] = dist.Bucket60_70,
                ["50-60"] = dist.Bucket50_60,
                ["40-50"] = dist.Bucket40_50,
                ["30-40"] = dist.Bucket30_40,
                ["20-30"] = dist.Bucket20_30,
                ["10-20"] = dist.Bucket10_20,
                ["0-10"] = dist.Bucket0_10
            };

            foreach (var (bucket, count) in buckets)
            {
                TrustMetrics.ScoreBucketCount.WithLabels(bucket).Set(count);
            }

            _logger.LogDebug("Trust score distribution (batched): avg={Avg:F1}, median={Median:F1}, stddev={StdDev:F2}, count={Count}",
                dist.Avg, dist.Median, dist.StdDev, dist.TotalCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect trust score distribution metrics");
            TrustMetrics.CollectionErrors.WithLabels("score_distribution").Inc();
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
    /// Collects network health metrics in a single query.
    /// </summary>
    private async Task CollectNetworkHealthBatchedAsync(CancellationToken ct)
    {
        try
        {
            var health = await _repository.GetNetworkHealthBatchedAsync(ct);

            // Velocity
            TrustMetrics.TrustVelocity.WithLabels("24h").Set(health.Velocity24h);
            TrustMetrics.TrustVelocity.WithLabels("7d").Set(health.Velocity7d);
            TrustMetrics.TrustVelocity.WithLabels("30d").Set(health.Velocity30d);

            // Churn
            TrustMetrics.TrustChurn.WithLabels("24h").Set(health.Churn24h);
            TrustMetrics.TrustChurn.WithLabels("7d").Set(health.Churn7d);
            TrustMetrics.TrustChurn.WithLabels("30d").Set(health.Churn30d);

            // Net change
            TrustMetrics.TrustNetChange.WithLabels("24h").Set(health.Velocity24h - health.Churn24h);
            TrustMetrics.TrustNetChange.WithLabels("7d").Set(health.Velocity7d - health.Churn7d);
            TrustMetrics.TrustNetChange.WithLabels("30d").Set(health.Velocity30d - health.Churn30d);

            // Graph metrics
            TrustMetrics.TrustReciprocityRate.Set(health.ReciprocityRate);
            TrustMetrics.TrustGraphDensity.Set(health.GraphDensity);
            TrustMetrics.AvgOutDegree.Set(health.AvgOutDegree);
            TrustMetrics.AvgInDegree.Set(health.AvgInDegree);

            _logger.LogDebug("Trust network health: vel_24h={V24h}, vel_7d={V7d}, vel_30d={V30d}, reciprocity={Reciprocity:F1}%",
                health.Velocity24h, health.Velocity7d, health.Velocity30d, health.ReciprocityRate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect network health metrics");
            TrustMetrics.CollectionErrors.WithLabels("network_health").Inc();
        }
    }

    /// <summary>
    /// Collects anomaly detection metrics in optimized queries.
    /// </summary>
    private async Task CollectAnomalyDetectionBatchedAsync(CancellationToken ct)
    {
        try
        {
            var anomaly = await _repository.GetAnomalyDetectionBatchedAsync(20, 30, ct);

            // Score drops
            TrustMetrics.ScoreDrops.WithLabels("24h").Set(anomaly.ScoreDrops24h);
            TrustMetrics.ScoreDrops.WithLabels("7d").Set(anomaly.ScoreDrops7d);

            if (anomaly.ScoreDrops24h > 0)
            {
                _logger.LogInformation("Detected {Count} significant trust score drops in last 24h", anomaly.ScoreDrops24h);
            }

            // Score spikes
            TrustMetrics.ScoreSpikes.WithLabels("24h").Set(anomaly.ScoreSpikes24h);
            TrustMetrics.ScoreSpikes.WithLabels("7d").Set(anomaly.ScoreSpikes7d);

            if (anomaly.ScoreSpikes24h > 10)
            {
                _logger.LogWarning("Detected {Count} suspicious trust score spikes in last 24h", anomaly.ScoreSpikes24h);
            }

            // Low trust new accounts
            TrustMetrics.LowTrustNewAccounts.WithLabels("24h").Set(anomaly.LowTrustNew24h);
            TrustMetrics.LowTrustNewAccounts.WithLabels("7d").Set(anomaly.LowTrustNew7d);

            // Penalized accounts
            TrustMetrics.PenalizedAccounts.Set(anomaly.PenalizedAccounts);

            _logger.LogDebug("Trust anomaly detection: drops_24h={Drops}, spikes_24h={Spikes}, penalized={Penalized}",
                anomaly.ScoreDrops24h, anomaly.ScoreSpikes24h, anomaly.PenalizedAccounts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect anomaly detection metrics");
            TrustMetrics.CollectionErrors.WithLabels("anomaly_detection").Inc();
        }
    }

    /// <summary>
    /// Collects economic-trust correlation metrics in a single query.
    /// </summary>
    private async Task CollectEconomicCorrelationBatchedAsync(CancellationToken ct)
    {
        try
        {
            var metrics = await _repository.GetEconomicCorrelationBatchedAsync(ct);

            // 24h window
            TrustMetrics.HighTrustVolume.WithLabels("24h").Set(metrics.HighTrustVolume24h);
            TrustMetrics.LowTrustVolume.WithLabels("24h").Set(metrics.LowTrustVolume24h);
            TrustMetrics.TrustWeightedVolume.WithLabels("24h").Set(metrics.WeightedVolume24h);

            var highTrustShare24h = metrics.TotalVolume24h > 0
                ? (metrics.HighTrustVolume24h / metrics.TotalVolume24h) * 100
                : 0;
            TrustMetrics.HighTrustVolumeShare.WithLabels("24h").Set(highTrustShare24h);

            // 7d window
            TrustMetrics.HighTrustVolume.WithLabels("7d").Set(metrics.HighTrustVolume7d);
            TrustMetrics.LowTrustVolume.WithLabels("7d").Set(metrics.LowTrustVolume7d);
            TrustMetrics.TrustWeightedVolume.WithLabels("7d").Set(metrics.WeightedVolume7d);

            var highTrustShare7d = metrics.TotalVolume7d > 0
                ? (metrics.HighTrustVolume7d / metrics.TotalVolume7d) * 100
                : 0;
            TrustMetrics.HighTrustVolumeShare.WithLabels("7d").Set(highTrustShare7d);

            _logger.LogDebug("Trust-volume correlation: 24h_high={High24h:F0}, 24h_share={Share24h:F1}%, 7d_high={High7d:F0}",
                metrics.HighTrustVolume24h, highTrustShare24h, metrics.HighTrustVolume7d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect trust volume metrics");
            TrustMetrics.CollectionErrors.WithLabels("economic_correlation").Inc();
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
