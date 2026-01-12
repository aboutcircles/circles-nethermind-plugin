using System.Diagnostics;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Background service that periodically collects liquidity metrics and updates Prometheus gauges.
/// Tracks Balancer vault balances, group treasuries, drain detection (multi-factor anomalies),
/// and whale transfers.
/// </summary>
public class LiquidityCollectorService : BackgroundService
{
    private readonly LiquidityRepository _repository;
    private readonly ILogger<LiquidityCollectorService> _logger;
    private readonly TimeSpan _collectionInterval;

    // Multi-factor drain detection thresholds (configurable)
    private readonly double _zScoreWarningThreshold;
    private readonly double _zScoreCriticalThreshold;
    private readonly double _minBalancePercentage;
    private readonly decimal _minAbsoluteAmountCrc;
    private readonly double _rateAccelerationFactor;

    // Sybil detection thresholds (configurable)
    private readonly int _sybilMinUniqueRecipients;
    private readonly decimal _sybilMinTotalWithdrawnCrc;
    private readonly decimal _sybilMaxAvgWithdrawalCrc;
    private readonly double _sybilRecipientMultiplier;

    // Default thresholds
    private const double DefaultZScoreWarningThreshold = -3.0;
    private const double DefaultZScoreCriticalThreshold = -4.0;
    private const double DefaultMinBalancePercentage = 10.0; // 10% of balance
    private const decimal DefaultMinAbsoluteAmountCrc = 100m; // 100 CRC minimum
    private const double DefaultRateAccelerationFactor = 3.0; // 3x average daily withdrawal

    // Sybil detection defaults
    private const int DefaultSybilMinUniqueRecipients = 10; // Many unique recipients
    private const decimal DefaultSybilMinTotalWithdrawnCrc = 500m; // Material total amount
    private const decimal DefaultSybilMaxAvgWithdrawalCrc = 50m; // Small avg withdrawal size
    private const double DefaultSybilRecipientMultiplier = 3.0; // 3x normal recipient count

    public LiquidityCollectorService(
        LiquidityRepository repository,
        ILogger<LiquidityCollectorService> logger,
        IConfiguration configuration)
    {
        _repository = repository;
        _logger = logger;

        // Liquidity metrics collection interval (default: 5 minutes)
        var intervalSeconds = configuration.GetValue<int>("Metrics:LiquidityCollectionIntervalSeconds", 300);
        _collectionInterval = TimeSpan.FromSeconds(intervalSeconds);

        // Multi-factor drain detection thresholds (configurable via appsettings)
        _zScoreWarningThreshold = configuration.GetValue<double>("Metrics:DrainDetection:ZScoreWarningThreshold", DefaultZScoreWarningThreshold);
        _zScoreCriticalThreshold = configuration.GetValue<double>("Metrics:DrainDetection:ZScoreCriticalThreshold", DefaultZScoreCriticalThreshold);
        _minBalancePercentage = configuration.GetValue<double>("Metrics:DrainDetection:MinBalancePercentage", DefaultMinBalancePercentage);
        _minAbsoluteAmountCrc = configuration.GetValue<decimal>("Metrics:DrainDetection:MinAbsoluteAmountCrc", DefaultMinAbsoluteAmountCrc);
        _rateAccelerationFactor = configuration.GetValue<double>("Metrics:DrainDetection:RateAccelerationFactor", DefaultRateAccelerationFactor);

        // Sybil detection thresholds
        _sybilMinUniqueRecipients = configuration.GetValue<int>("Metrics:SybilDetection:MinUniqueRecipients", DefaultSybilMinUniqueRecipients);
        _sybilMinTotalWithdrawnCrc = configuration.GetValue<decimal>("Metrics:SybilDetection:MinTotalWithdrawnCrc", DefaultSybilMinTotalWithdrawnCrc);
        _sybilMaxAvgWithdrawalCrc = configuration.GetValue<decimal>("Metrics:SybilDetection:MaxAvgWithdrawalCrc", DefaultSybilMaxAvgWithdrawalCrc);
        _sybilRecipientMultiplier = configuration.GetValue<double>("Metrics:SybilDetection:RecipientMultiplier", DefaultSybilRecipientMultiplier);

        _logger.LogInformation(
            "Drain detection configured: ZScoreCritical={Critical}, ZScoreWarning={Warning}, MinBalance%={MinPct}, MinAmount={MinAmt}CRC, RateAccel={Rate}x",
            _zScoreCriticalThreshold, _zScoreWarningThreshold, _minBalancePercentage, _minAbsoluteAmountCrc, _rateAccelerationFactor);
        _logger.LogInformation(
            "Sybil detection configured: MinRecipients={MinRec}, MinTotal={MinTotal}CRC, MaxAvg={MaxAvg}CRC, RecipientMult={Mult}x",
            _sybilMinUniqueRecipients, _sybilMinTotalWithdrawnCrc, _sybilMaxAvgWithdrawalCrc, _sybilRecipientMultiplier);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Liquidity Collector starting with {Interval}s interval",
            _collectionInterval.TotalSeconds);

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
            CollectAggregateTvlMetricsAsync(ct),
            CollectDrainDetectionMetricsAsync(ct),
            CollectGroupTreasuryDrainDetectionAsync(ct),
            CollectSybilDetectionMetricsAsync(ct),
            CollectWhaleTransferMetricsAsync(ct)
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
                    .WithLabels(treasury.GroupAddress, treasury.Name)
                    .Set((double)treasury.TreasuryTotal);

                // Set member count
                LiquidityMetrics.GroupMemberCount
                    .WithLabels(treasury.GroupAddress, treasury.Name)
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
    /// Collects aggregate TVL metrics for Balancer and Group Treasuries.
    /// These are the primary metrics used for alerting (simpler, less noisy than per-token).
    /// Includes both 1h and 15m windows for different alert sensitivity.
    /// </summary>
    private async Task CollectAggregateTvlMetricsAsync(CancellationToken ct)
    {
        try
        {
            // Collect Balancer TVL (includes 1h and 15m changes)
            var balancerTvl = await _repository.GetBalancerTvlAsync(ct);
            LiquidityMetrics.BalancerTvlTotal.Set((double)balancerTvl.CurrentTotalCrc);
            LiquidityMetrics.BalancerTvlChange1h.Set((double)balancerTvl.Change1hCrc);
            LiquidityMetrics.BalancerTvlChangePct1h.Set(balancerTvl.Change1hPct);
            LiquidityMetrics.BalancerTvlChange15m.Set((double)balancerTvl.Change15mCrc);
            LiquidityMetrics.BalancerTvlChangePct15m.Set(balancerTvl.Change15mPct);

            _logger.LogDebug("Balancer TVL: {Total:F2} CRC, 1h: {Change1h:F2}% ({Abs1h:F2} CRC), 15m: {Change15m:F2}% ({Abs15m:F2} CRC)",
                balancerTvl.CurrentTotalCrc, balancerTvl.Change1hPct, balancerTvl.Change1hCrc,
                balancerTvl.Change15mPct, balancerTvl.Change15mCrc);

            // Collect Group Treasury TVL (includes 1h and 15m changes)
            var treasuryTvl = await _repository.GetGroupTreasuryTvlAsync(ct);
            LiquidityMetrics.GroupTreasuryTvlTotal.Set((double)treasuryTvl.CurrentTotalCrc);
            LiquidityMetrics.GroupTreasuryTvlChange1h.Set((double)treasuryTvl.Change1hCrc);
            LiquidityMetrics.GroupTreasuryTvlChangePct1h.Set(treasuryTvl.Change1hPct);
            LiquidityMetrics.GroupTreasuryTvlChange15m.Set((double)treasuryTvl.Change15mCrc);
            LiquidityMetrics.GroupTreasuryTvlChangePct15m.Set(treasuryTvl.Change15mPct);

            _logger.LogDebug("Group Treasury TVL: {Total:F2} CRC, 1h: {Change1h:F2}% ({Abs1h:F2} CRC), 15m: {Change15m:F2}% ({Abs15m:F2} CRC)",
                treasuryTvl.CurrentTotalCrc, treasuryTvl.Change1hPct, treasuryTvl.Change1hCrc,
                treasuryTvl.Change15mPct, treasuryTvl.Change15mCrc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect aggregate TVL metrics");
            LiquidityMetrics.LiquidityCollectionErrors.WithLabels("aggregate_tvl").Inc();
        }
    }

    /// <summary>
    /// Collects multi-factor anomaly detection metrics for drain detection.
    /// Uses a combination of:
    /// - Z-score (statistical anomaly vs 30-day history)
    /// - Percentage of balance withdrawn
    /// - Absolute amount in CRC
    /// - Rate acceleration (spike vs gradual trend)
    /// </summary>
    private async Task CollectDrainDetectionMetricsAsync(CancellationToken ct)
    {
        try
        {
            var zScores = await _repository.GetBalancerVaultZScoresAsync(ct);

            foreach (var score in zScores)
            {
                // Set 1-hour change metric
                LiquidityMetrics.BalancerVaultChange1h
                    .WithLabels(score.TokenAddress, score.TokenName)
                    .Set((double)score.LatestChange);

                // Set z-score metric
                LiquidityMetrics.BalancerVaultZScore1h
                    .WithLabels(score.TokenAddress, score.TokenName)
                    .Set(score.ZScore);

                // Multi-factor drain detection
                var severity = EvaluateDrainSeverity(score);

                switch (severity)
                {
                    case DrainSeverity.Critical:
                        SetAnomalyMetrics(score, "critical");
                        _logger.LogWarning(
                            "CRITICAL drain detected for {TokenName} ({Token}): z-score={ZScore:F2}, " +
                            "withdrawal={Change:F2}CRC ({Pct:F1}% of balance), rate={Rate:F1}x avg",
                            score.TokenName, score.TokenAddress, score.ZScore,
                            Math.Abs(score.LatestChangeCrc), score.BalancePercentage, score.RateAcceleration);
                        break;

                    case DrainSeverity.Warning:
                        SetAnomalyMetrics(score, "warning");
                        _logger.LogWarning(
                            "Unusual outflow for {TokenName} ({Token}): z-score={ZScore:F2}, " +
                            "withdrawal={Change:F2}CRC ({Pct:F1}% of balance)",
                            score.TokenName, score.TokenAddress, score.ZScore,
                            Math.Abs(score.LatestChangeCrc), score.BalancePercentage);
                        break;

                    default:
                        ClearAnomalyMetrics(score);
                        break;
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
    /// Evaluates drain severity using multi-factor scoring.
    /// Critical requires: z-score breach + significant % of balance + minimum absolute amount.
    /// Warning requires: z-score breach only (for visibility).
    /// </summary>
    private DrainSeverity EvaluateDrainSeverity(TokenZScore score)
    {
        // Not an outflow? No alert.
        if (score.LatestChange >= 0)
            return DrainSeverity.None;

        var absChangeCrc = Math.Abs(score.LatestChangeCrc);

        // Critical: Multi-factor check
        // All conditions must be met for critical alert
        if (score.ZScore < _zScoreCriticalThreshold &&
            score.BalancePercentage >= _minBalancePercentage &&
            absChangeCrc >= _minAbsoluteAmountCrc)
        {
            // Optional: Check rate acceleration for extra confidence
            // If withdrawal is N times larger than average daily outflow, it's more suspicious
            if (score.RateAcceleration >= _rateAccelerationFactor)
            {
                return DrainSeverity.Critical;
            }

            // Even without rate acceleration, if other factors are severe enough, still critical
            if (score.ZScore < _zScoreCriticalThreshold * 1.5) // e.g., z < -6 with default -4 threshold
            {
                return DrainSeverity.Critical;
            }
        }

        // Warning: Just z-score breach (for monitoring, not alerting)
        if (score.ZScore < _zScoreWarningThreshold)
        {
            return DrainSeverity.Warning;
        }

        return DrainSeverity.None;
    }

    private void SetAnomalyMetrics(TokenZScore score, string severity)
    {
        var isCritical = severity == "critical";

        LiquidityMetrics.BalancerVaultAnomaly
            .WithLabels(score.TokenAddress, score.TokenName, "critical")
            .Set(isCritical ? 1 : 0);
        LiquidityMetrics.BalancerVaultAnomaly
            .WithLabels(score.TokenAddress, score.TokenName, "warning")
            .Set(isCritical ? 0 : 1);
        LiquidityMetrics.BalancerVaultDrainEvents
            .WithLabels(score.TokenAddress, score.TokenName, severity)
            .Inc();
    }

    private void ClearAnomalyMetrics(TokenZScore score)
    {
        LiquidityMetrics.BalancerVaultAnomaly
            .WithLabels(score.TokenAddress, score.TokenName, "warning")
            .Set(0);
        LiquidityMetrics.BalancerVaultAnomaly
            .WithLabels(score.TokenAddress, score.TokenName, "critical")
            .Set(0);
    }

    private enum DrainSeverity
    {
        None,
        Warning,
        Critical
    }

    /// <summary>
    /// Collects group treasury drain detection metrics using the same multi-factor approach.
    /// </summary>
    private async Task CollectGroupTreasuryDrainDetectionAsync(CancellationToken ct)
    {
        try
        {
            var zScores = await _repository.GetGroupTreasuryZScoresAsync(ct);

            foreach (var score in zScores)
            {
                // Set z-score metric
                LiquidityMetrics.GroupTreasuryZScore
                    .WithLabels(score.GroupAddress, score.GroupName)
                    .Set(score.ZScore);

                // Multi-factor drain detection (reuse same logic)
                var severity = EvaluateGroupTreasurySeverity(score);

                switch (severity)
                {
                    case DrainSeverity.Critical:
                        SetGroupAnomalyMetrics(score, "critical");
                        _logger.LogWarning(
                            "CRITICAL group treasury drain for {GroupName} ({Group}): z-score={ZScore:F2}, " +
                            "withdrawal={Change:F2}CRC ({Pct:F1}% of treasury), rate={Rate:F1}x avg",
                            score.GroupName, score.GroupAddress, score.ZScore,
                            Math.Abs(score.LatestChangeCrc), score.BalancePercentage, score.RateAcceleration);
                        break;

                    case DrainSeverity.Warning:
                        SetGroupAnomalyMetrics(score, "warning");
                        _logger.LogWarning(
                            "Unusual group treasury outflow for {GroupName} ({Group}): z-score={ZScore:F2}, " +
                            "withdrawal={Change:F2}CRC ({Pct:F1}% of treasury)",
                            score.GroupName, score.GroupAddress, score.ZScore,
                            Math.Abs(score.LatestChangeCrc), score.BalancePercentage);
                        break;

                    default:
                        ClearGroupAnomalyMetrics(score);
                        break;
                }
            }

            _logger.LogDebug("Collected group treasury drain detection for {GroupCount} groups", zScores.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect group treasury drain detection metrics");
            LiquidityMetrics.LiquidityCollectionErrors.WithLabels("group_treasury_drain").Inc();
        }
    }

    private DrainSeverity EvaluateGroupTreasurySeverity(GroupTreasuryZScore score)
    {
        if (score.LatestChange >= 0)
            return DrainSeverity.None;

        var absChangeCrc = Math.Abs(score.LatestChangeCrc);

        if (score.ZScore < _zScoreCriticalThreshold &&
            score.BalancePercentage >= _minBalancePercentage &&
            absChangeCrc >= _minAbsoluteAmountCrc)
        {
            if (score.RateAcceleration >= _rateAccelerationFactor ||
                score.ZScore < _zScoreCriticalThreshold * 1.5)
            {
                return DrainSeverity.Critical;
            }
        }

        if (score.ZScore < _zScoreWarningThreshold)
            return DrainSeverity.Warning;

        return DrainSeverity.None;
    }

    private void SetGroupAnomalyMetrics(GroupTreasuryZScore score, string severity)
    {
        var isCritical = severity == "critical";
        LiquidityMetrics.GroupTreasuryAnomaly
            .WithLabels(score.GroupAddress, score.GroupName, "critical")
            .Set(isCritical ? 1 : 0);
        LiquidityMetrics.GroupTreasuryAnomaly
            .WithLabels(score.GroupAddress, score.GroupName, "warning")
            .Set(isCritical ? 0 : 1);
        LiquidityMetrics.GroupTreasuryDrainEvents
            .WithLabels(score.GroupAddress, score.GroupName, severity)
            .Inc();
    }

    private void ClearGroupAnomalyMetrics(GroupTreasuryZScore score)
    {
        LiquidityMetrics.GroupTreasuryAnomaly
            .WithLabels(score.GroupAddress, score.GroupName, "warning")
            .Set(0);
        LiquidityMetrics.GroupTreasuryAnomaly
            .WithLabels(score.GroupAddress, score.GroupName, "critical")
            .Set(0);
    }

    /// <summary>
    /// Detects sybil-style coordinated attacks: many small withdrawals to many unique recipients.
    /// </summary>
    private async Task CollectSybilDetectionMetricsAsync(CancellationToken ct)
    {
        try
        {
            var sybilMetrics = await _repository.GetHourlySybilMetricsAsync(ct);

            foreach (var metric in sybilMetrics)
            {
                // Set observability metrics
                LiquidityMetrics.SybilWithdrawalCount
                    .WithLabels(metric.VaultAddress)
                    .Set(metric.WithdrawalCount);
                LiquidityMetrics.SybilUniqueRecipients
                    .WithLabels(metric.VaultAddress)
                    .Set(metric.UniqueRecipients);
                LiquidityMetrics.SybilTotalWithdrawn
                    .WithLabels(metric.VaultAddress)
                    .Set((double)metric.TotalWithdrawnCrc);
                LiquidityMetrics.SybilAvgWithdrawalSize
                    .WithLabels(metric.VaultAddress)
                    .Set((double)metric.AvgWithdrawalCrc);

                // Evaluate sybil attack pattern
                var severity = EvaluateSybilSeverity(metric);

                switch (severity)
                {
                    case DrainSeverity.Critical:
                        SetSybilAnomalyMetrics(metric, "critical");
                        _logger.LogWarning(
                            "CRITICAL sybil attack pattern on vault {Vault}: {Recipients} unique recipients, " +
                            "{Total:F2}CRC total ({Avg:F2}CRC avg), {Mult:F1}x baseline recipients",
                            metric.VaultAddress, metric.UniqueRecipients, metric.TotalWithdrawnCrc,
                            metric.AvgWithdrawalCrc,
                            metric.BaselineHourlyRecipients > 0
                                ? metric.UniqueRecipients / metric.BaselineHourlyRecipients
                                : 0);
                        break;

                    case DrainSeverity.Warning:
                        SetSybilAnomalyMetrics(metric, "warning");
                        _logger.LogWarning(
                            "Unusual withdrawal pattern on vault {Vault}: {Recipients} unique recipients, " +
                            "{Total:F2}CRC total",
                            metric.VaultAddress, metric.UniqueRecipients, metric.TotalWithdrawnCrc);
                        break;

                    default:
                        ClearSybilAnomalyMetrics(metric);
                        break;
                }
            }

            _logger.LogDebug("Collected sybil detection metrics for {VaultCount} vaults", sybilMetrics.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect sybil detection metrics");
            LiquidityMetrics.LiquidityCollectionErrors.WithLabels("sybil_detection").Inc();
        }
    }

    private DrainSeverity EvaluateSybilSeverity(SybilDetectionResult metric)
    {
        // Calculate recipient multiplier vs baseline
        var recipientMultiplier = metric.BaselineHourlyRecipients > 0
            ? metric.UniqueRecipients / metric.BaselineHourlyRecipients
            : 0;

        // Critical: Many unique recipients + material total + small avg size + spike vs baseline
        if (metric.UniqueRecipients >= _sybilMinUniqueRecipients &&
            metric.TotalWithdrawnCrc >= _sybilMinTotalWithdrawnCrc &&
            metric.AvgWithdrawalCrc <= _sybilMaxAvgWithdrawalCrc &&
            recipientMultiplier >= _sybilRecipientMultiplier)
        {
            return DrainSeverity.Critical;
        }

        // Warning: Elevated recipients or unusual pattern
        if ((metric.UniqueRecipients >= _sybilMinUniqueRecipients / 2 &&
             metric.TotalWithdrawnCrc >= _sybilMinTotalWithdrawnCrc / 2) ||
            recipientMultiplier >= _sybilRecipientMultiplier)
        {
            return DrainSeverity.Warning;
        }

        return DrainSeverity.None;
    }

    private void SetSybilAnomalyMetrics(SybilDetectionResult metric, string severity)
    {
        var isCritical = severity == "critical";
        LiquidityMetrics.SybilAnomaly
            .WithLabels(metric.VaultAddress, "critical")
            .Set(isCritical ? 1 : 0);
        LiquidityMetrics.SybilAnomaly
            .WithLabels(metric.VaultAddress, "warning")
            .Set(isCritical ? 0 : 1);
        LiquidityMetrics.SybilAlertEvents
            .WithLabels(metric.VaultAddress, severity)
            .Inc();
    }

    private void ClearSybilAnomalyMetrics(SybilDetectionResult metric)
    {
        LiquidityMetrics.SybilAnomaly
            .WithLabels(metric.VaultAddress, "warning")
            .Set(0);
        LiquidityMetrics.SybilAnomaly
            .WithLabels(metric.VaultAddress, "critical")
            .Set(0);
    }

    /// <summary>
    /// Collects whale transfer metrics - large individual transfers to/from Balancer vault.
    /// </summary>
    private async Task CollectWhaleTransferMetricsAsync(CancellationToken ct)
    {
        try
        {
            var transfers = await _repository.GetRecentWhaleTransfersAsync(100, ct);

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
                    .Set(transfer.Timestamp);

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
}
