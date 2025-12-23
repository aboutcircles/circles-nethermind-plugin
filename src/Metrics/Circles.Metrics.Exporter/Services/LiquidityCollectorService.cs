using System.Diagnostics;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Background service that periodically collects liquidity metrics and updates Prometheus gauges.
/// Tracks Balancer vault balances, group treasuries, drain detection (z-score anomalies),
/// whale transfers, and arbitrage bot activity.
/// </summary>
public class LiquidityCollectorService : BackgroundService
{
    private readonly LiquidityRepository _repository;
    private readonly ILogger<LiquidityCollectorService> _logger;
    private readonly TimeSpan _collectionInterval;

    /// <summary>
    /// Whale transfer threshold in wei (1e20 = 100 tokens).
    /// Transfers larger than this are tracked individually.
    /// </summary>
    private readonly decimal _whaleThreshold;

    /// <summary>
    /// Z-score threshold for warning-level anomalies.
    /// A z-score below this indicates unusual outflow (2 standard deviations).
    /// </summary>
    private const double ZScoreWarningThreshold = -2.0;

    /// <summary>
    /// Z-score threshold for critical-level anomalies.
    /// A z-score below this indicates severe drain (3 standard deviations).
    /// </summary>
    private const double ZScoreCriticalThreshold = -3.0;

    public LiquidityCollectorService(
        LiquidityRepository repository,
        ILogger<LiquidityCollectorService> logger,
        IConfiguration configuration)
    {
        _repository = repository;
        _logger = logger;

        // Liquidity metrics collection interval (default: 5 minutes)
        // More frequent than KPI collection since liquidity changes can be rapid
        var intervalSeconds = configuration.GetValue<int>("Metrics:LiquidityCollectionIntervalSeconds", 300);
        _collectionInterval = TimeSpan.FromSeconds(intervalSeconds);

        // Whale threshold from config (default: 1e20 = 100 tokens)
        _whaleThreshold = configuration.GetValue<decimal>("Metrics:WhaleThresholdWei", 100_000_000_000_000_000_000m);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Liquidity Collector starting with {Interval}s interval, whale threshold: {Threshold}",
            _collectionInterval.TotalSeconds, _whaleThreshold);

        // Wait for app startup
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                await CollectAllLiquidityMetricsAsync(stoppingToken);

                sw.Stop();
                LiquidityMetrics.LiquidityCollectionDuration.Inc(sw.Elapsed.TotalSeconds);
                LiquidityMetrics.LiquidityLastCollectionTimestamp.Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                _logger.LogDebug("Liquidity collection completed in {Duration}ms", sw.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during liquidity collection");
            }

            await Task.Delay(_collectionInterval, stoppingToken);
        }
    }

    private async Task CollectAllLiquidityMetricsAsync(CancellationToken ct)
    {
        // Run independent collections in parallel
        await Task.WhenAll(
            CollectBalancerVaultMetricsAsync(ct),
            CollectGroupTreasuryMetricsAsync(ct),
            CollectDrainDetectionMetricsAsync(ct),
            CollectWhaleTransferMetricsAsync(ct),
            CollectArbbotMetricsAsync(ct)
        );
    }

    /// <summary>
    /// Collects current Balancer vault balance metrics.
    /// </summary>
    private async Task CollectBalancerVaultMetricsAsync(CancellationToken ct)
    {
        try
        {
            var balances = await _repository.GetBalancerVaultBalancesAsync(ct);

            decimal totalBalance = 0;
            int tokenCount = 0;

            foreach (var balance in balances)
            {
                // Set per-token balance gauge
                LiquidityMetrics.BalancerVaultBalance
                    .WithLabels(balance.TokenAddress, balance.TokenName ?? "unknown")
                    .Set((double)balance.Balance);

                totalBalance += balance.Balance;
                tokenCount++;
            }

            // Set aggregate metrics
            LiquidityMetrics.BalancerVaultBalanceTotal.Set((double)totalBalance);
            LiquidityMetrics.BalancerVaultTokensCount.Set(tokenCount);

            _logger.LogDebug("Collected Balancer vault metrics: {TokenCount} tokens, total balance: {Total}",
                tokenCount, totalBalance);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect Balancer vault metrics");
            LiquidityMetrics.LiquidityCollectionErrors.WithLabels("balancer_vault").Inc();
        }
    }

    /// <summary>
    /// Collects group treasury balance and membership metrics.
    /// </summary>
    private async Task CollectGroupTreasuryMetricsAsync(CancellationToken ct)
    {
        try
        {
            var treasuries = await _repository.GetGroupTreasuriesAsync(ct);

            foreach (var treasury in treasuries)
            {
                // Set per-group total treasury balance
                LiquidityMetrics.GroupTreasuryTotal
                    .WithLabels(treasury.GroupAddress, treasury.GroupName ?? "unknown")
                    .Set((double)treasury.TotalBalance);

                // Set member count
                LiquidityMetrics.GroupMemberCount
                    .WithLabels(treasury.GroupAddress, treasury.GroupName ?? "unknown")
                    .Set(treasury.MemberCount);
            }

            _logger.LogDebug("Collected group treasury metrics for {GroupCount} groups", treasuries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect group treasury metrics");
            LiquidityMetrics.LiquidityCollectionErrors.WithLabels("group_treasury").Inc();
        }
    }

    /// <summary>
    /// Collects z-score based anomaly detection metrics for drain detection.
    /// Z-score measures how many standard deviations the current hourly change
    /// is from the 30-day historical mean.
    /// </summary>
    private async Task CollectDrainDetectionMetricsAsync(CancellationToken ct)
    {
        try
        {
            var zScores = await _repository.GetBalancerVaultZScoresAsync(ct);

            foreach (var score in zScores)
            {
                // Set 1-hour change
                LiquidityMetrics.BalancerVaultChange1h
                    .WithLabels(score.TokenAddress)
                    .Set((double)score.LatestChange);

                // Set z-score
                LiquidityMetrics.BalancerVaultZScore1h
                    .WithLabels(score.TokenAddress)
                    .Set(score.ZScore);

                // Determine anomaly severity and update metrics
                if (score.ZScore < ZScoreCriticalThreshold)
                {
                    // Critical anomaly (potential drain)
                    LiquidityMetrics.BalancerVaultAnomaly
                        .WithLabels(score.TokenAddress, "critical")
                        .Set(1);
                    LiquidityMetrics.BalancerVaultAnomaly
                        .WithLabels(score.TokenAddress, "warning")
                        .Set(0);
                    LiquidityMetrics.BalancerVaultDrainEvents
                        .WithLabels(score.TokenAddress, "critical")
                        .Inc();

                    _logger.LogWarning("CRITICAL drain detected for {Token}: z-score={ZScore:F2}, change={Change}",
                        score.TokenAddress, score.ZScore, score.LatestChange);
                }
                else if (score.ZScore < ZScoreWarningThreshold)
                {
                    // Warning-level anomaly
                    LiquidityMetrics.BalancerVaultAnomaly
                        .WithLabels(score.TokenAddress, "warning")
                        .Set(1);
                    LiquidityMetrics.BalancerVaultAnomaly
                        .WithLabels(score.TokenAddress, "critical")
                        .Set(0);
                    LiquidityMetrics.BalancerVaultDrainEvents
                        .WithLabels(score.TokenAddress, "warning")
                        .Inc();

                    _logger.LogWarning("Unusual outflow for {Token}: z-score={ZScore:F2}, change={Change}",
                        score.TokenAddress, score.ZScore, score.LatestChange);
                }
                else
                {
                    // No anomaly
                    LiquidityMetrics.BalancerVaultAnomaly
                        .WithLabels(score.TokenAddress, "warning")
                        .Set(0);
                    LiquidityMetrics.BalancerVaultAnomaly
                        .WithLabels(score.TokenAddress, "critical")
                        .Set(0);
                }
            }

            _logger.LogDebug("Collected drain detection metrics for {TokenCount} tokens", zScores.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect drain detection metrics");
            LiquidityMetrics.LiquidityCollectionErrors.WithLabels("drain_detection").Inc();
        }
    }

    /// <summary>
    /// Collects whale transfer metrics - large individual transfers to/from Balancer vault.
    /// </summary>
    private async Task CollectWhaleTransferMetricsAsync(CancellationToken ct)
    {
        try
        {
            var transfers = await _repository.GetRecentWhaleTransfersAsync(_whaleThreshold, ct);

            foreach (var transfer in transfers)
            {
                // Increment transfer count
                LiquidityMetrics.WhaleTransferTotal
                    .WithLabels(transfer.TokenAddress, transfer.Direction)
                    .Inc();

                // Add to volume counter
                LiquidityMetrics.WhaleTransferVolume
                    .WithLabels(transfer.TokenAddress, transfer.Direction)
                    .Inc((double)transfer.Amount);

                // Update last transfer gauge (most recent)
                LiquidityMetrics.WhaleTransferLast
                    .WithLabels(transfer.TokenAddress, transfer.From, transfer.To, transfer.Direction)
                    .Set((double)transfer.Amount);

                // Update timestamp
                LiquidityMetrics.WhaleTransferTimestamp
                    .WithLabels(transfer.TokenAddress)
                    .Set(transfer.Timestamp.ToUnixTimeSeconds());

                _logger.LogInformation("Whale transfer: {Direction} {Amount} of {Token} from {From} to {To}",
                    transfer.Direction, transfer.Amount, transfer.TokenAddress, transfer.From, transfer.To);
            }

            _logger.LogDebug("Processed {TransferCount} whale transfers", transfers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect whale transfer metrics");
            LiquidityMetrics.LiquidityCollectionErrors.WithLabels("whale_transfers").Inc();
        }
    }

    /// <summary>
    /// Collects arbitrage bot activity metrics from the logger database.
    /// </summary>
    private async Task CollectArbbotMetricsAsync(CancellationToken ct)
    {
        try
        {
            var stats = await _repository.GetArbbotStatsAsync(ct);

            if (stats != null)
            {
                // Quote counts
                LiquidityMetrics.ArbbotQuotesTotal
                    .WithLabels("success")
                    .Inc(stats.SuccessfulQuotes);
                LiquidityMetrics.ArbbotQuotesTotal
                    .WithLabels("failed")
                    .Inc(stats.FailedQuotes);

                // Quotes per minute rate
                LiquidityMetrics.ArbbotQuotesRate1m.Set(stats.QuotesPerMinute);

                // Opportunities found
                LiquidityMetrics.ArbbotOpportunitiesFound.Inc(stats.OpportunitiesFound);

                _logger.LogDebug("Collected arbbot stats: {Quotes} quotes ({Rate}/min), {Opportunities} opportunities",
                    stats.SuccessfulQuotes + stats.FailedQuotes, stats.QuotesPerMinute, stats.OpportunitiesFound);
            }
        }
        catch (Exception ex)
        {
            // Arbbot metrics are optional - logger DB may not be available
            _logger.LogDebug(ex, "Failed to collect arbbot metrics (logger DB may not be configured)");
            LiquidityMetrics.LiquidityCollectionErrors.WithLabels("arbbot").Inc();
        }
    }
}
