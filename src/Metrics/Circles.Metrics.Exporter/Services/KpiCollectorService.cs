using System.Diagnostics;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Background service that periodically collects Circles KPIs and updates Prometheus metrics.
/// </summary>
public class KpiCollectorService : BackgroundService
{
    private readonly KpiRepository _repository;
    private readonly PriceService _priceService;
    private readonly ILogger<KpiCollectorService> _logger;
    private readonly TimeSpan _collectionInterval;

    // Time windows for metrics
    private static readonly TimeSpan Window24H = TimeSpan.FromHours(24);
    private static readonly TimeSpan Window7D = TimeSpan.FromDays(7);
    private static readonly TimeSpan Window14D = TimeSpan.FromDays(14);
    private static readonly TimeSpan Window30D = TimeSpan.FromDays(30);
    private static readonly TimeSpan Window90D = TimeSpan.FromDays(90);
    private static readonly TimeSpan Window180D = TimeSpan.FromDays(180);
    private static readonly TimeSpan Window1Y = TimeSpan.FromDays(365);

    // Cached values for USD calculations
    private double _lastCrcPriceUsd;
    private double _lastTokenOfferPriceInCrc;

    public KpiCollectorService(
        KpiRepository repository,
        PriceService priceService,
        ILogger<KpiCollectorService> logger,
        IConfiguration configuration)
    {
        _repository = repository;
        _priceService = priceService;
        _logger = logger;

        var intervalSeconds = configuration.GetValue<int>("Metrics:CollectionIntervalSeconds", 60);
        _collectionInterval = TimeSpan.FromSeconds(intervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("KPI Collector starting with {Interval}s interval", _collectionInterval.TotalSeconds);

        // Wait a bit for the app to start
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                await CollectAllKpisAsync(stoppingToken);

                sw.Stop();
                BusinessKpiMetrics.CollectionDuration.Inc(sw.Elapsed.TotalSeconds);
                BusinessKpiMetrics.LastCollectionTimestamp.Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                _logger.LogDebug("KPI collection completed in {Duration}ms", sw.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during KPI collection");
            }

            await Task.Delay(_collectionInterval, stoppingToken);
        }
    }

    private async Task CollectAllKpisAsync(CancellationToken ct)
    {
        // Phase 1: Run batched queries (4 queries instead of ~60)
        await CollectBatchedMetricsAsync(ct);

        // Phase 2: Collect Token Offers metrics first (needed for price calculation)
        // This sets _lastTokenOfferPriceInCrc which is used by price metrics
        await CollectTokenOffersMetricsAsync(ct);

        // Phase 3: Run remaining individual queries in parallel
        // Note: Some methods have partial overlap with batched queries (harmless, just sets same value twice)
        // but they also contain unique metrics not covered by batched queries.
        // CollectUserMetricsAsync is fully covered by batched, so excluded.
        // CollectAdvancedMonetaryMetricsAsync has partial overlap but contains unique metrics:
        //   - UserRetentionRate, FirstTimeTransactors, TransferSizePercentile
        //   - Extended windows (180d, 1y) for MoneyVelocity, NetInflow, MicroTx, LargeTx
        await Task.WhenAll(
            CollectTrustMetricsAsync(ct),
            CollectGroupMetricsAsync(ct),
            CollectEconomicMetricsAsync(ct),
            CollectActivityRatesAsync(ct),
            CollectNetworkHealthMetricsAsync(ct),
            CollectAccountTypeMetricsAsync(ct),
            CollectPaymentGatewayMetricsAsync(ct),
            CollectPriceAndEcosystemValueMetricsAsync(ct),
            CollectProfileMetricsAsync(ct),           // Unique: ProfilesCreated 24h
            CollectDuneParityMetricsAsync(ct),        // Unique: MintingFraction14d
            CollectSybilDetectionMetricsAsync(ct),    // Unique: BatchRegistrations, MintAndDrain, HighVolumeInviters
            CollectAdvancedMonetaryMetricsAsync(ct)   // Unique: UserRetention, FirstTimeTx, TransferPercentiles, extended windows
        );
    }

    /// <summary>
    /// Collects metrics using batched queries to reduce database round-trips.
    /// </summary>
    private async Task CollectBatchedMetricsAsync(CancellationToken ct)
    {
        // Entity counts (replaces 7 individual queries)
        try
        {
            var counts = await _repository.GetEntityCountsBatchedAsync(ct);
            BusinessKpiMetrics.TotalHumans.WithLabels("v1").Set(counts.HumansV1);
            BusinessKpiMetrics.TotalHumans.WithLabels("v2").Set(counts.HumansV2);
            BusinessKpiMetrics.TotalOrganizations.Set(counts.Organizations);
            BusinessKpiMetrics.TotalGroups.Set(counts.Groups);
            BusinessKpiMetrics.TotalBackers.Set(counts.Backers);
            BusinessKpiMetrics.ProfilesCreated.WithLabels("total").Set(counts.ProfilesTotal);
            BusinessKpiMetrics.ActiveTrusts.Set(counts.ActiveTrusts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect batched entity counts");
            BusinessKpiMetrics.CollectionErrors.WithLabels("batched_entity_counts").Inc();
        }

        // Time-windowed counts (replaces ~36 individual queries)
        try
        {
            var windowed = await _repository.GetTimeWindowedCountsBatchedAsync(ct);

            // New users
            foreach (var (window, count) in windowed.NewUsers)
                BusinessKpiMetrics.NewUsers.WithLabels(window).Set(count);

            // New organizations
            foreach (var (window, count) in windowed.NewOrganizations)
                BusinessKpiMetrics.NewOrganizations.WithLabels(window).Set(count);

            // New groups
            foreach (var (window, count) in windowed.NewGroups)
                BusinessKpiMetrics.NewGroups.WithLabels(window).Set(count);

            // New backers
            foreach (var (window, count) in windowed.NewBackers)
                BusinessKpiMetrics.NewBackers.WithLabels(window).Set(count);

            // Active minters
            foreach (var (window, count) in windowed.ActiveMinters)
                BusinessKpiMetrics.ActiveMinters.WithLabels(window).Set(count);

            // Transfer counts
            foreach (var (window, count) in windowed.TransferCounts)
                BusinessKpiMetrics.TransferCount.WithLabels(window).Set(count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect batched time-windowed counts");
            BusinessKpiMetrics.CollectionErrors.WithLabels("batched_time_windowed").Inc();
        }

        // Economic metrics (replaces ~12 individual queries)
        try
        {
            var economic = await _repository.GetEconomicMetricsBatchedAsync(ct);
            BusinessKpiMetrics.TotalCrcSupply.Set(economic.TotalSupply);
            BusinessKpiMetrics.TotalMintedAllTime.Set(economic.TotalMintedAllTime);
            BusinessKpiMetrics.DailyMintVolume.Set(economic.DailyMintVolume);
            BusinessKpiMetrics.DailyTransferVolume.Set(economic.DailyTransferVolume);
            BusinessKpiMetrics.DailyMintCount.Set(economic.DailyMintCount);
            BusinessKpiMetrics.ActiveBalanceHolders.Set(economic.ActiveBalanceHolders);
            BusinessKpiMetrics.AverageBalance.Set(economic.AverageBalance);
            BusinessKpiMetrics.MedianBalance.Set(economic.MedianBalance);
            BusinessKpiMetrics.GiniCoefficient.Set(economic.GiniCoefficient);
            BusinessKpiMetrics.DailyActiveWallets.Set(economic.DailyActiveWallets);
            BusinessKpiMetrics.WeeklyActiveWallets.Set(economic.WeeklyActiveWallets);
            BusinessKpiMetrics.MonthlyActiveWallets.Set(economic.MonthlyActiveWallets);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect batched economic metrics");
            BusinessKpiMetrics.CollectionErrors.WithLabels("batched_economic").Inc();
        }

        // Sybil metrics (replaces ~5 individual queries)
        try
        {
            var sybil = await _repository.GetSybilMetricsBatchedAsync(ct);
            BusinessKpiMetrics.AccountsWithoutProfile.Set(sybil.AccountsWithoutProfile);
            BusinessKpiMetrics.AccountsWithoutIncomingTrust.Set(sybil.AccountsWithoutIncomingTrust);
            BusinessKpiMetrics.SuspiciousAccounts.Set(sybil.SuspiciousAccounts);
            BusinessKpiMetrics.OrganicAccounts.Set(sybil.OrganicAccounts);
            BusinessKpiMetrics.IsolatedAccounts.Set(sybil.IsolatedAccounts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect batched sybil metrics");
            BusinessKpiMetrics.CollectionErrors.WithLabels("batched_sybil").Inc();
        }

        // Advanced monetary metrics (replaces ~14 individual queries)
        try
        {
            var monetary = await _repository.GetAdvancedMonetaryMetricsBatchedAsync(ct);
            BusinessKpiMetrics.MoneyVelocity.WithLabels("7d").Set(monetary.MoneyVelocity7d);
            BusinessKpiMetrics.MoneyVelocity.WithLabels("30d").Set(monetary.MoneyVelocity30d);
            BusinessKpiMetrics.MoneyVelocity.WithLabels("90d").Set(monetary.MoneyVelocity90d);
            BusinessKpiMetrics.TopHolderConcentration.WithLabels("10").Set(monetary.TopHolderConcentration10);
            BusinessKpiMetrics.TopHolderConcentration.WithLabels("100").Set(monetary.TopHolderConcentration100);
            BusinessKpiMetrics.TopHolderConcentration.WithLabels("1000").Set(monetary.TopHolderConcentration1000);
            BusinessKpiMetrics.NetInflow.WithLabels("24h").Set(monetary.NetInflow24h);
            BusinessKpiMetrics.NetInflow.WithLabels("7d").Set(monetary.NetInflow7d);
            BusinessKpiMetrics.NetInflow.WithLabels("30d").Set(monetary.NetInflow30d);
            BusinessKpiMetrics.MicroTransactionCount.WithLabels("24h").Set(monetary.MicroTransactions24h);
            BusinessKpiMetrics.LargeTransactionCount.WithLabels("24h").Set(monetary.LargeTransactions24h);
            BusinessKpiMetrics.DemurragePaid.WithLabels("24h").Set(monetary.DemurragePaid24h);
            BusinessKpiMetrics.DemurragePaid.WithLabels("7d").Set(monetary.DemurragePaid7d);
            BusinessKpiMetrics.DemurragePaid.WithLabels("30d").Set(monetary.DemurragePaid30d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect batched advanced monetary metrics");
            BusinessKpiMetrics.CollectionErrors.WithLabels("batched_advanced_monetary").Inc();
        }
    }

    private async Task CollectUserMetricsAsync(CancellationToken ct)
    {
        try
        {
            var v1Humans = await _repository.GetTotalHumansV1Async(ct);
            BusinessKpiMetrics.TotalHumans.WithLabels("v1").Set(v1Humans);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect V1 humans metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("total_humans_v1").Inc();
        }

        try
        {
            var v2Humans = await _repository.GetTotalHumansV2Async(ct);
            BusinessKpiMetrics.TotalHumans.WithLabels("v2").Set(v2Humans);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect V2 humans metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("total_humans_v2").Inc();
        }

        try
        {
            var orgs = await _repository.GetTotalOrganizationsAsync(ct);
            BusinessKpiMetrics.TotalOrganizations.Set(orgs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect organizations metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("total_organizations").Inc();
        }

        try
        {
            var groups = await _repository.GetTotalGroupsAsync(ct);
            BusinessKpiMetrics.TotalGroups.Set(groups);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect groups metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("total_groups").Inc();
        }

        try
        {
            var newUsers24h = await _repository.GetNewUsersAsync(Window24H, ct);
            var newUsers7d = await _repository.GetNewUsersAsync(Window7D, ct);
            var newUsers30d = await _repository.GetNewUsersAsync(Window30D, ct);
            var newUsers90d = await _repository.GetNewUsersAsync(Window90D, ct);
            var newUsers180d = await _repository.GetNewUsersAsync(Window180D, ct);
            var newUsers1y = await _repository.GetNewUsersAsync(Window1Y, ct);

            BusinessKpiMetrics.NewUsers.WithLabels("24h").Set(newUsers24h);
            BusinessKpiMetrics.NewUsers.WithLabels("7d").Set(newUsers7d);
            BusinessKpiMetrics.NewUsers.WithLabels("30d").Set(newUsers30d);
            BusinessKpiMetrics.NewUsers.WithLabels("90d").Set(newUsers90d);
            BusinessKpiMetrics.NewUsers.WithLabels("180d").Set(newUsers180d);
            BusinessKpiMetrics.NewUsers.WithLabels("1y").Set(newUsers1y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect new users metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("new_users").Inc();
        }
    }

    private async Task CollectTrustMetricsAsync(CancellationToken ct)
    {
        try
        {
            var activeTrusts = await _repository.GetActiveTrustsAsync(ct);
            BusinessKpiMetrics.ActiveTrusts.Set(activeTrusts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect active trusts metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("active_trusts").Inc();
        }

        try
        {
            var newTrusts24h = await _repository.GetNewTrustsAsync(Window24H, ct);
            var newTrusts7d = await _repository.GetNewTrustsAsync(Window7D, ct);

            BusinessKpiMetrics.NewTrusts.WithLabels("24h").Set(newTrusts24h);
            BusinessKpiMetrics.NewTrusts.WithLabels("7d").Set(newTrusts7d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect new trusts metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("new_trusts").Inc();
        }

        try
        {
            var (added24h, removed24h) = await _repository.GetTrustChangesAsync(Window24H, ct);
            BusinessKpiMetrics.TrustChanges.WithLabels("24h", "added").Set(added24h);
            BusinessKpiMetrics.TrustChanges.WithLabels("24h", "removed").Set(removed24h);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect trust changes metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("trust_changes").Inc();
        }
    }

    private async Task CollectEconomicMetricsAsync(CancellationToken ct)
    {
        try
        {
            var backers = await _repository.GetTotalBackersAsync(ct);
            BusinessKpiMetrics.TotalBackers.Set(backers);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect backers metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("total_backers").Inc();
        }

        try
        {
            var minters24h = await _repository.GetActiveMintersAsync(Window24H, ct);
            var minters7d = await _repository.GetActiveMintersAsync(Window7D, ct);
            var minters30d = await _repository.GetActiveMintersAsync(Window30D, ct);
            var minters90d = await _repository.GetActiveMintersAsync(Window90D, ct);
            var minters180d = await _repository.GetActiveMintersAsync(Window180D, ct);
            var minters1y = await _repository.GetActiveMintersAsync(Window1Y, ct);

            BusinessKpiMetrics.ActiveMinters.WithLabels("24h").Set(minters24h);
            BusinessKpiMetrics.ActiveMinters.WithLabels("7d").Set(minters7d);
            BusinessKpiMetrics.ActiveMinters.WithLabels("30d").Set(minters30d);
            BusinessKpiMetrics.ActiveMinters.WithLabels("90d").Set(minters90d);
            BusinessKpiMetrics.ActiveMinters.WithLabels("180d").Set(minters180d);
            BusinessKpiMetrics.ActiveMinters.WithLabels("1y").Set(minters1y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect active minters metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("active_minters").Inc();
        }

        try
        {
            var mintVolume = await _repository.GetDailyMintVolumeAsync(ct);
            BusinessKpiMetrics.DailyMintVolume.Set((double)mintVolume);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect mint volume metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("daily_mint_volume").Inc();
        }

        try
        {
            var transferVolume = await _repository.GetDailyTransferVolumeAsync(ct);
            BusinessKpiMetrics.DailyTransferVolume.Set((double)transferVolume);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect transfer volume metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("daily_transfer_volume").Inc();
        }

        try
        {
            var transferCount = await _repository.GetDailyTransferCountAsync(ct);
            BusinessKpiMetrics.DailyTransferCount.Set(transferCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect transfer count metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("daily_transfer_count").Inc();
        }

        try
        {
            var addresses24h = await _repository.GetUniqueTransactingAddressesAsync(Window24H, ct);
            var addresses7d = await _repository.GetUniqueTransactingAddressesAsync(Window7D, ct);

            BusinessKpiMetrics.UniqueTransactingAddresses.WithLabels("24h").Set(addresses24h);
            BusinessKpiMetrics.UniqueTransactingAddresses.WithLabels("7d").Set(addresses7d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect unique addresses metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("unique_addresses").Inc();
        }
    }

    private async Task CollectGroupMetricsAsync(CancellationToken ct)
    {
        try
        {
            var members = await _repository.GetGroupMembersTotalAsync(ct);
            BusinessKpiMetrics.GroupMembersTotal.Set(members);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect group members metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("group_members").Inc();
        }

        try
        {
            var mintVolume24h = await _repository.GetGroupMintVolumeAsync(Window24H, ct);
            BusinessKpiMetrics.GroupMintVolume.WithLabels("24h").Set((double)mintVolume24h);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect group mint volume metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("group_mint_volume").Inc();
        }
    }

    private async Task CollectProfileMetricsAsync(CancellationToken ct)
    {
        try
        {
            var profilesTotal = await _repository.GetProfilesTotalAsync(ct);
            BusinessKpiMetrics.ProfilesCreated.WithLabels("total").Set(profilesTotal);

            var profiles24h = await _repository.GetProfilesCreatedAsync(Window24H, ct);
            BusinessKpiMetrics.ProfilesCreated.WithLabels("24h").Set(profiles24h);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect profiles metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("profiles_created").Inc();
        }
    }

    // ============================================================================
    // NEW: Dune Parity KPIs
    // ============================================================================

    private async Task CollectDuneParityMetricsAsync(CancellationToken ct)
    {
        try
        {
            var dailyMintCount = await _repository.GetDailyMintCountAsync(ct);
            BusinessKpiMetrics.DailyMintCount.Set(dailyMintCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect daily mint count metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("daily_mint_count").Inc();
        }

        try
        {
            var newBackers24h = await _repository.GetNewBackersAsync(Window24H, ct);
            var newBackers7d = await _repository.GetNewBackersAsync(Window7D, ct);
            var newBackers30d = await _repository.GetNewBackersAsync(Window30D, ct);
            var newBackers90d = await _repository.GetNewBackersAsync(Window90D, ct);
            var newBackers180d = await _repository.GetNewBackersAsync(Window180D, ct);
            var newBackers1y = await _repository.GetNewBackersAsync(Window1Y, ct);

            BusinessKpiMetrics.NewBackers.WithLabels("24h").Set(newBackers24h);
            BusinessKpiMetrics.NewBackers.WithLabels("7d").Set(newBackers7d);
            BusinessKpiMetrics.NewBackers.WithLabels("30d").Set(newBackers30d);
            BusinessKpiMetrics.NewBackers.WithLabels("90d").Set(newBackers90d);
            BusinessKpiMetrics.NewBackers.WithLabels("180d").Set(newBackers180d);
            BusinessKpiMetrics.NewBackers.WithLabels("1y").Set(newBackers1y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect new backers metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("new_backers").Inc();
        }

        try
        {
            var mintingFraction = await _repository.GetMintingFraction14DayAsync(ct);
            BusinessKpiMetrics.MintingFraction14d.Set(mintingFraction);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect minting fraction metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("minting_fraction_14d").Inc();
        }

        try
        {
            var newOrgs24h = await _repository.GetNewOrganizationsAsync(Window24H, ct);
            var newOrgs7d = await _repository.GetNewOrganizationsAsync(Window7D, ct);
            var newOrgs30d = await _repository.GetNewOrganizationsAsync(Window30D, ct);
            var newOrgs90d = await _repository.GetNewOrganizationsAsync(Window90D, ct);
            var newOrgs180d = await _repository.GetNewOrganizationsAsync(Window180D, ct);
            var newOrgs1y = await _repository.GetNewOrganizationsAsync(Window1Y, ct);

            BusinessKpiMetrics.NewOrganizations.WithLabels("24h").Set(newOrgs24h);
            BusinessKpiMetrics.NewOrganizations.WithLabels("7d").Set(newOrgs7d);
            BusinessKpiMetrics.NewOrganizations.WithLabels("30d").Set(newOrgs30d);
            BusinessKpiMetrics.NewOrganizations.WithLabels("90d").Set(newOrgs90d);
            BusinessKpiMetrics.NewOrganizations.WithLabels("180d").Set(newOrgs180d);
            BusinessKpiMetrics.NewOrganizations.WithLabels("1y").Set(newOrgs1y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect new organizations metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("new_organizations").Inc();
        }

        try
        {
            var newGroups24h = await _repository.GetNewGroupsAsync(Window24H, ct);
            var newGroups7d = await _repository.GetNewGroupsAsync(Window7D, ct);
            var newGroups30d = await _repository.GetNewGroupsAsync(Window30D, ct);
            var newGroups90d = await _repository.GetNewGroupsAsync(Window90D, ct);
            var newGroups180d = await _repository.GetNewGroupsAsync(Window180D, ct);
            var newGroups1y = await _repository.GetNewGroupsAsync(Window1Y, ct);

            BusinessKpiMetrics.NewGroups.WithLabels("24h").Set(newGroups24h);
            BusinessKpiMetrics.NewGroups.WithLabels("7d").Set(newGroups7d);
            BusinessKpiMetrics.NewGroups.WithLabels("30d").Set(newGroups30d);
            BusinessKpiMetrics.NewGroups.WithLabels("90d").Set(newGroups90d);
            BusinessKpiMetrics.NewGroups.WithLabels("180d").Set(newGroups180d);
            BusinessKpiMetrics.NewGroups.WithLabels("1y").Set(newGroups1y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect new groups metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("new_groups").Inc();
        }
    }

    // ============================================================================
    // Activity Rates (Minters/Spenders by window)
    // ============================================================================

    private async Task CollectActivityRatesAsync(CancellationToken ct)
    {
        // Minting rates by window
        try
        {
            var rate14d = await _repository.GetMintingRateAsync(Window14D, ct);
            var rate30d = await _repository.GetMintingRateAsync(Window30D, ct);
            var rate90d = await _repository.GetMintingRateAsync(Window90D, ct);
            var rate180d = await _repository.GetMintingRateAsync(Window180D, ct);
            var rate1y = await _repository.GetMintingRateAsync(Window1Y, ct);

            BusinessKpiMetrics.MintingRate.WithLabels("14d").Set(rate14d);
            BusinessKpiMetrics.MintingRate.WithLabels("30d").Set(rate30d);
            BusinessKpiMetrics.MintingRate.WithLabels("90d").Set(rate90d);
            BusinessKpiMetrics.MintingRate.WithLabels("180d").Set(rate180d);
            BusinessKpiMetrics.MintingRate.WithLabels("1y").Set(rate1y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect minting rate metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("minting_rate").Inc();
        }

        // Spending rates by window
        try
        {
            var rate14d = await _repository.GetSpendingRateAsync(Window14D, ct);
            var rate30d = await _repository.GetSpendingRateAsync(Window30D, ct);
            var rate90d = await _repository.GetSpendingRateAsync(Window90D, ct);
            var rate180d = await _repository.GetSpendingRateAsync(Window180D, ct);
            var rate1y = await _repository.GetSpendingRateAsync(Window1Y, ct);

            BusinessKpiMetrics.SpendingRate.WithLabels("14d").Set(rate14d);
            BusinessKpiMetrics.SpendingRate.WithLabels("30d").Set(rate30d);
            BusinessKpiMetrics.SpendingRate.WithLabels("90d").Set(rate90d);
            BusinessKpiMetrics.SpendingRate.WithLabels("180d").Set(rate180d);
            BusinessKpiMetrics.SpendingRate.WithLabels("1y").Set(rate1y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect spending rate metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("spending_rate").Inc();
        }

        // Transfer volume by window
        try
        {
            var vol7d = await _repository.GetTransferVolumeAsync(Window7D, ct);
            var vol30d = await _repository.GetTransferVolumeAsync(Window30D, ct);
            var vol90d = await _repository.GetTransferVolumeAsync(Window90D, ct);
            var vol180d = await _repository.GetTransferVolumeAsync(Window180D, ct);
            var vol1y = await _repository.GetTransferVolumeAsync(Window1Y, ct);

            BusinessKpiMetrics.TransferVolume.WithLabels("7d").Set(vol7d);
            BusinessKpiMetrics.TransferVolume.WithLabels("30d").Set(vol30d);
            BusinessKpiMetrics.TransferVolume.WithLabels("90d").Set(vol90d);
            BusinessKpiMetrics.TransferVolume.WithLabels("180d").Set(vol180d);
            BusinessKpiMetrics.TransferVolume.WithLabels("1y").Set(vol1y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect transfer volume metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("transfer_volume").Inc();
        }

        // Mint volume by window
        try
        {
            var vol7d = await _repository.GetMintVolumeAsync(Window7D, ct);
            var vol30d = await _repository.GetMintVolumeAsync(Window30D, ct);
            var vol90d = await _repository.GetMintVolumeAsync(Window90D, ct);

            BusinessKpiMetrics.MintVolume.WithLabels("7d").Set(vol7d);
            BusinessKpiMetrics.MintVolume.WithLabels("30d").Set(vol30d);
            BusinessKpiMetrics.MintVolume.WithLabels("90d").Set(vol90d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect mint volume metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("mint_volume").Inc();
        }

        // Transfer count by window
        try
        {
            var count24h = await _repository.GetTransferCountAsync(Window24H, ct);
            var count7d = await _repository.GetTransferCountAsync(Window7D, ct);
            var count30d = await _repository.GetTransferCountAsync(Window30D, ct);
            var count90d = await _repository.GetTransferCountAsync(Window90D, ct);
            var count180d = await _repository.GetTransferCountAsync(Window180D, ct);
            var count1y = await _repository.GetTransferCountAsync(Window1Y, ct);

            BusinessKpiMetrics.TransferCount.WithLabels("24h").Set(count24h);
            BusinessKpiMetrics.TransferCount.WithLabels("7d").Set(count7d);
            BusinessKpiMetrics.TransferCount.WithLabels("30d").Set(count30d);
            BusinessKpiMetrics.TransferCount.WithLabels("90d").Set(count90d);
            BusinessKpiMetrics.TransferCount.WithLabels("180d").Set(count180d);
            BusinessKpiMetrics.TransferCount.WithLabels("1y").Set(count1y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect transfer count metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("transfer_count").Inc();
        }

        // Average transfer amount by window
        try
        {
            var avg24h = await _repository.GetAverageTransferAmountAsync(Window24H, ct);
            var avg7d = await _repository.GetAverageTransferAmountAsync(Window7D, ct);
            var avg30d = await _repository.GetAverageTransferAmountAsync(Window30D, ct);

            BusinessKpiMetrics.AverageTransferAmount.WithLabels("24h").Set(avg24h);
            BusinessKpiMetrics.AverageTransferAmount.WithLabels("7d").Set(avg7d);
            BusinessKpiMetrics.AverageTransferAmount.WithLabels("30d").Set(avg30d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect average transfer amount metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("average_transfer_amount").Inc();
        }

        // Median transfer amount by window
        try
        {
            var median24h = await _repository.GetMedianTransferAmountAsync(Window24H, ct);
            var median7d = await _repository.GetMedianTransferAmountAsync(Window7D, ct);
            var median30d = await _repository.GetMedianTransferAmountAsync(Window30D, ct);

            BusinessKpiMetrics.MedianTransferAmount.WithLabels("24h").Set(median24h);
            BusinessKpiMetrics.MedianTransferAmount.WithLabels("7d").Set(median7d);
            BusinessKpiMetrics.MedianTransferAmount.WithLabels("30d").Set(median30d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect median transfer amount metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("median_transfer_amount").Inc();
        }
    }

    // ============================================================================
    // Sybil Detection Metrics
    // ============================================================================

    private async Task CollectSybilDetectionMetricsAsync(CancellationToken ct)
    {
        try
        {
            var noProfile = await _repository.GetAccountsWithoutProfileAsync(ct);
            BusinessKpiMetrics.AccountsWithoutProfile.Set(noProfile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect accounts without profile metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("accounts_no_profile").Inc();
        }

        try
        {
            var noTrust = await _repository.GetAccountsWithoutIncomingTrustAsync(ct);
            BusinessKpiMetrics.AccountsWithoutIncomingTrust.Set(noTrust);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect accounts without trust metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("accounts_no_trust").Inc();
        }

        try
        {
            // Batch registrations (accounts in same block with > 5 registrations)
            var batch24h = await _repository.GetBatchRegistrationsAsync(Window24H, 5, ct);
            var batch7d = await _repository.GetBatchRegistrationsAsync(Window7D, 5, ct);

            BusinessKpiMetrics.BatchRegistrations.WithLabels("24h").Set(batch24h);
            BusinessKpiMetrics.BatchRegistrations.WithLabels("7d").Set(batch7d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect batch registrations metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("batch_registrations").Inc();
        }

        try
        {
            var mintDrain24h = await _repository.GetMintAndDrainAccountsAsync(Window24H, ct);
            var mintDrain7d = await _repository.GetMintAndDrainAccountsAsync(Window7D, ct);

            BusinessKpiMetrics.MintAndDrainAccounts.WithLabels("24h").Set(mintDrain24h);
            BusinessKpiMetrics.MintAndDrainAccounts.WithLabels("7d").Set(mintDrain7d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect mint-and-drain metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("mint_and_drain").Inc();
        }

        try
        {
            // High-volume inviters (> 10 invitees in window)
            var hvInviters24h = await _repository.GetHighVolumeInvitersCountAsync(Window24H, 10, ct);
            var hvInviters7d = await _repository.GetHighVolumeInvitersCountAsync(Window7D, 10, ct);

            BusinessKpiMetrics.HighVolumeInviters.WithLabels("24h").Set(hvInviters24h);
            BusinessKpiMetrics.HighVolumeInviters.WithLabels("7d").Set(hvInviters7d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect high-volume inviters metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("high_volume_inviters").Inc();
        }

        try
        {
            var suspicious = await _repository.GetSuspiciousAccountsAsync(ct);
            BusinessKpiMetrics.SuspiciousAccounts.Set(suspicious);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect suspicious accounts metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("suspicious_accounts").Inc();
        }

        try
        {
            var organic = await _repository.GetOrganicAccountsAsync(ct);
            BusinessKpiMetrics.OrganicAccounts.Set(organic);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect organic accounts metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("organic_accounts").Inc();
        }
    }

    // ============================================================================
    // Network Health Metrics
    // ============================================================================

    private async Task CollectNetworkHealthMetricsAsync(CancellationToken ct)
    {
        try
        {
            var avgTrust = await _repository.GetAverageTrustConnectionsAsync(ct);
            BusinessKpiMetrics.AverageTrustConnections.Set(avgTrust);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect average trust connections metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("avg_trust_connections").Inc();
        }

        try
        {
            var isolated = await _repository.GetIsolatedAccountsAsync(ct);
            BusinessKpiMetrics.IsolatedAccounts.Set(isolated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect isolated accounts metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("isolated_accounts").Inc();
        }
    }

    // ============================================================================
    // Advanced Monetary/Economic Metrics (Extended windows and complex queries)
    // Core metrics are in GetAdvancedMonetaryMetricsBatchedAsync:
    //   - MoneyVelocity 7d/30d/90d, TopHolderConcentration 10/100/1000
    //   - NetInflow 24h/7d/30d, MicroTx/LargeTx 24h, Demurrage 24h/7d/30d
    // This method only collects metrics NOT in batch.
    // ============================================================================

    private async Task CollectAdvancedMonetaryMetricsAsync(CancellationToken ct)
    {
        // Extended windows for Money velocity (180d, 1y) - core windows in batch
        try
        {
            var vel180d = await _repository.GetMoneyVelocityAsync(Window180D, ct);
            var vel1y = await _repository.GetMoneyVelocityAsync(Window1Y, ct);

            BusinessKpiMetrics.MoneyVelocity.WithLabels("180d").Set(vel180d);
            BusinessKpiMetrics.MoneyVelocity.WithLabels("1y").Set(vel1y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect extended money velocity metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("money_velocity_extended").Inc();
        }

        // User retention rate (complex query, not in batch)
        try
        {
            var ret7d = await _repository.GetUserRetentionRateAsync(Window7D, ct);
            var ret30d = await _repository.GetUserRetentionRateAsync(Window30D, ct);
            var ret90d = await _repository.GetUserRetentionRateAsync(Window90D, ct);

            BusinessKpiMetrics.UserRetentionRate.WithLabels("7d").Set(ret7d);
            BusinessKpiMetrics.UserRetentionRate.WithLabels("30d").Set(ret30d);
            BusinessKpiMetrics.UserRetentionRate.WithLabels("90d").Set(ret90d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect user retention rate metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("user_retention_rate").Inc();
        }

        // First-time transactors (complex query, not in batch)
        try
        {
            var first24h = await _repository.GetFirstTimeTransactorsAsync(Window24H, ct);
            var first7d = await _repository.GetFirstTimeTransactorsAsync(Window7D, ct);
            var first30d = await _repository.GetFirstTimeTransactorsAsync(Window30D, ct);
            var first90d = await _repository.GetFirstTimeTransactorsAsync(Window90D, ct);
            var first180d = await _repository.GetFirstTimeTransactorsAsync(Window180D, ct);
            var first1y = await _repository.GetFirstTimeTransactorsAsync(Window1Y, ct);

            BusinessKpiMetrics.FirstTimeTransactors.WithLabels("24h").Set(first24h);
            BusinessKpiMetrics.FirstTimeTransactors.WithLabels("7d").Set(first7d);
            BusinessKpiMetrics.FirstTimeTransactors.WithLabels("30d").Set(first30d);
            BusinessKpiMetrics.FirstTimeTransactors.WithLabels("90d").Set(first90d);
            BusinessKpiMetrics.FirstTimeTransactors.WithLabels("180d").Set(first180d);
            BusinessKpiMetrics.FirstTimeTransactors.WithLabels("1y").Set(first1y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect first-time transactors metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("first_time_transactors").Inc();
        }

        // Transfer size distribution percentiles (complex query, not in batch)
        try
        {
            var p10_24h = await _repository.GetTransferSizeDistributionPercentileAsync(0.10, Window24H, ct);
            var p25_24h = await _repository.GetTransferSizeDistributionPercentileAsync(0.25, Window24H, ct);
            var p75_24h = await _repository.GetTransferSizeDistributionPercentileAsync(0.75, Window24H, ct);
            var p90_24h = await _repository.GetTransferSizeDistributionPercentileAsync(0.90, Window24H, ct);

            BusinessKpiMetrics.TransferSizePercentile.WithLabels("p10", "24h").Set(p10_24h);
            BusinessKpiMetrics.TransferSizePercentile.WithLabels("p25", "24h").Set(p25_24h);
            BusinessKpiMetrics.TransferSizePercentile.WithLabels("p75", "24h").Set(p75_24h);
            BusinessKpiMetrics.TransferSizePercentile.WithLabels("p90", "24h").Set(p90_24h);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect transfer size percentile metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("transfer_size_percentile").Inc();
        }

        // Extended windows for Micro/Large transaction counts (7d) - 24h in batch
        try
        {
            var micro7d = await _repository.GetMicroTransactionCountAsync(Window7D, 1.0, ct);
            var large7d = await _repository.GetLargeTransactionCountAsync(Window7D, 100.0, ct);

            BusinessKpiMetrics.MicroTransactionCount.WithLabels("7d").Set(micro7d);
            BusinessKpiMetrics.LargeTransactionCount.WithLabels("7d").Set(large7d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect extended transaction size bucket metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("transaction_size_buckets_extended").Inc();
        }

        // Extended windows for Net inflow (90d, 180d, 1y) - 24h/7d/30d in batch
        try
        {
            var inflow90d = await _repository.GetNetInflowAsync(Window90D, ct);
            var inflow180d = await _repository.GetNetInflowAsync(Window180D, ct);
            var inflow1y = await _repository.GetNetInflowAsync(Window1Y, ct);

            BusinessKpiMetrics.NetInflow.WithLabels("90d").Set(inflow90d);
            BusinessKpiMetrics.NetInflow.WithLabels("180d").Set(inflow180d);
            BusinessKpiMetrics.NetInflow.WithLabels("1y").Set(inflow1y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect extended net inflow metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("net_inflow_extended").Inc();
        }
    }

    // ============================================================================
    // Account Type Breakdown Metrics (humans, groups, organizations)
    // ============================================================================

    private async Task CollectAccountTypeMetricsAsync(CancellationToken ct)
    {
        var accountTypes = new[] { "human", "group", "organization" };

        // Gini coefficient by account type
        try
        {
            foreach (var type in accountTypes)
            {
                var gini = await _repository.GetGiniCoefficientByTypeAsync(type, ct);
                BusinessKpiMetrics.GiniCoefficientByType.WithLabels(type).Set(gini);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect Gini coefficient by type metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("gini_by_type").Inc();
        }

        // Gini coefficient for non-custodial humans (excluding aggregators/exchanges)
        try
        {
            var giniNonCustodial = await _repository.GetGiniCoefficientNonCustodialAsync(ct: ct);
            BusinessKpiMetrics.GiniCoefficientNonCustodial.Set(giniNonCustodial);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect non-custodial Gini coefficient metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("gini_non_custodial").Inc();
        }

        // Top holder concentration for non-custodial humans (top 10, 100, 1000)
        try
        {
            int[] topNValues = [10, 100, 1000];
            foreach (var topN in topNValues)
            {
                var concentration = await _repository.GetTopHolderConcentrationNonCustodialAsync(topN, ct: ct);
                BusinessKpiMetrics.TopHolderConcentrationNonCustodial.WithLabels(topN.ToString()).Set(concentration);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect non-custodial top holder concentration metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("top_holder_non_custodial").Inc();
        }

        // Total balance by account type
        try
        {
            foreach (var type in accountTypes)
            {
                var balance = await _repository.GetTotalBalanceByTypeAsync(type, ct);
                BusinessKpiMetrics.TotalBalanceByType.WithLabels(type).Set(balance);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect total balance by type metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("balance_by_type").Inc();
        }

        // Balance holder count by account type
        try
        {
            foreach (var type in accountTypes)
            {
                var count = await _repository.GetBalanceHolderCountByTypeAsync(type, ct);
                BusinessKpiMetrics.BalanceHolderCountByType.WithLabels(type).Set(count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect balance holder count by type metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("holder_count_by_type").Inc();
        }

        // Average balance by account type
        try
        {
            foreach (var type in accountTypes)
            {
                var avg = await _repository.GetAverageBalanceByTypeAsync(type, ct);
                BusinessKpiMetrics.AverageBalanceByType.WithLabels(type).Set(avg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect average balance by type metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("avg_balance_by_type").Inc();
        }

        // Median balance by account type
        try
        {
            foreach (var type in accountTypes)
            {
                var median = await _repository.GetMedianBalanceByTypeAsync(type, ct);
                BusinessKpiMetrics.MedianBalanceByType.WithLabels(type).Set(median);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect median balance by type metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("median_balance_by_type").Inc();
        }

        // Top holder concentration by account type (top 10, 100, 1000 for each type)
        try
        {
            int[] topNValues = [10, 100, 1000];
            foreach (var type in accountTypes)
            {
                foreach (var topN in topNValues)
                {
                    var concentration = await _repository.GetTopHolderConcentrationByTypeAsync(topN, type, ct);
                    BusinessKpiMetrics.TopHolderConcentrationByType.WithLabels(topN.ToString(), type).Set(concentration);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect top holder concentration by type metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("top_holder_by_type").Inc();
        }

        // Supply share by account type (% of total CRC held by each type)
        try
        {
            foreach (var type in accountTypes)
            {
                var share = await _repository.GetSupplyShareByTypeAsync(type, ct);
                BusinessKpiMetrics.SupplyShareByType.WithLabels(type).Set(share);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect supply share by type metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("supply_share_by_type").Inc();
        }

        // Infrastructure vs Economic Actors holdings breakdown
        try
        {
            var holdings = await _repository.GetInfrastructureHoldingsAsync(ct);
            BusinessKpiMetrics.InfrastructureHoldingsBalance.Set(holdings.InfrastructureBalance);
            BusinessKpiMetrics.EconomicActorsHoldingsBalance.Set(holdings.EconomicActorsBalance);
            BusinessKpiMetrics.InfrastructureHoldingsPercentage.Set(holdings.InfrastructurePercentage);
            BusinessKpiMetrics.EconomicActorsHoldingsPercentage.Set(holdings.EconomicActorsPercentage);
            BusinessKpiMetrics.InfrastructureAddressCount.Set(holdings.InfrastructureAddressCount);
            BusinessKpiMetrics.EconomicActorsCount.Set(holdings.EconomicActorsCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect infrastructure holdings metrics");
            BusinessKpiMetrics.CollectionErrors.WithLabels("infrastructure_holdings").Inc();
        }
    }

    // ============================================================================
    // Token Offers Metrics (GNO Bonus, Marketplace)
    // ============================================================================

    private async Task CollectTokenOffersMetricsAsync(CancellationToken ct)
    {
        // Batched query for totals
        try
        {
            var metrics = await _repository.GetTokenOffersMetricsBatchedAsync(ct);

            BusinessKpiMetrics.TokenOfferCyclesTotal.Set(metrics.CyclesTotal);
            BusinessKpiMetrics.TokenOfferClaimsTotal.Set(metrics.ClaimsTotal);
            BusinessKpiMetrics.TokenOfferCrcSpentTotal.Set(metrics.CrcSpentTotal);
            BusinessKpiMetrics.TokenOfferTokensReceivedTotal.Set(metrics.TokensReceivedTotal);
            BusinessKpiMetrics.TokenOfferCurrentPriceInCrc.Set(metrics.CurrentPriceInCrc);
            BusinessKpiMetrics.TokenOfferCurrentLimitInCrc.Set(metrics.CurrentLimitInCrc);
            BusinessKpiMetrics.TokenOfferAcceptedCrcCount.Set(metrics.AcceptedCrcCount);

            // Calculate average CRC per claim
            var avgCrcPerClaim = metrics.ClaimsTotal > 0
                ? metrics.CrcSpentTotal / metrics.ClaimsTotal
                : 0;
            BusinessKpiMetrics.TokenOfferAvgCrcPerClaim.Set(avgCrcPerClaim);

            // Cache the offer price for USD calculations
            _lastTokenOfferPriceInCrc = metrics.CurrentPriceInCrc;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect batched token offers metrics");
            BusinessKpiMetrics.CollectionErrors.WithLabels("token_offers_batched").Inc();
        }

        // Time-windowed metrics
        try
        {
            var claims24h = await _repository.GetTokenOfferClaimsAsync(Window24H, ct);
            var claims7d = await _repository.GetTokenOfferClaimsAsync(Window7D, ct);
            var claims30d = await _repository.GetTokenOfferClaimsAsync(Window30D, ct);

            BusinessKpiMetrics.TokenOfferClaims.WithLabels("24h").Set(claims24h);
            BusinessKpiMetrics.TokenOfferClaims.WithLabels("7d").Set(claims7d);
            BusinessKpiMetrics.TokenOfferClaims.WithLabels("30d").Set(claims30d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect token offer claims metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("token_offer_claims").Inc();
        }

        try
        {
            var claimers24h = await _repository.GetTokenOfferUniqueClaimersAsync(Window24H, ct);
            var claimers7d = await _repository.GetTokenOfferUniqueClaimersAsync(Window7D, ct);
            var claimers30d = await _repository.GetTokenOfferUniqueClaimersAsync(Window30D, ct);

            BusinessKpiMetrics.TokenOfferUniqueClaimers.WithLabels("24h").Set(claimers24h);
            BusinessKpiMetrics.TokenOfferUniqueClaimers.WithLabels("7d").Set(claimers7d);
            BusinessKpiMetrics.TokenOfferUniqueClaimers.WithLabels("30d").Set(claimers30d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect token offer unique claimers metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("token_offer_claimers").Inc();
        }

        try
        {
            var crcSpent24h = await _repository.GetTokenOfferCrcSpentAsync(Window24H, ct);
            var crcSpent7d = await _repository.GetTokenOfferCrcSpentAsync(Window7D, ct);
            var crcSpent30d = await _repository.GetTokenOfferCrcSpentAsync(Window30D, ct);

            BusinessKpiMetrics.TokenOfferCrcSpent.WithLabels("24h").Set(crcSpent24h);
            BusinessKpiMetrics.TokenOfferCrcSpent.WithLabels("7d").Set(crcSpent7d);
            BusinessKpiMetrics.TokenOfferCrcSpent.WithLabels("30d").Set(crcSpent30d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect token offer CRC spent metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("token_offer_crc_spent").Inc();
        }

        try
        {
            var received24h = await _repository.GetTokenOfferTokensReceivedAsync(Window24H, ct);
            var received7d = await _repository.GetTokenOfferTokensReceivedAsync(Window7D, ct);
            var received30d = await _repository.GetTokenOfferTokensReceivedAsync(Window30D, ct);

            BusinessKpiMetrics.TokenOfferTokensReceived.WithLabels("24h").Set(received24h);
            BusinessKpiMetrics.TokenOfferTokensReceived.WithLabels("7d").Set(received7d);
            BusinessKpiMetrics.TokenOfferTokensReceived.WithLabels("30d").Set(received30d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect token offer tokens received metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("token_offer_received").Inc();
        }
    }

    // ============================================================================
    // Payment Gateway Metrics
    // ============================================================================

    private async Task CollectPaymentGatewayMetricsAsync(CancellationToken ct)
    {
        // Batched query for totals
        try
        {
            var metrics = await _repository.GetPaymentGatewayMetricsBatchedAsync(ct);

            BusinessKpiMetrics.PaymentGatewaysTotal.Set(metrics.GatewaysTotal);
            BusinessKpiMetrics.PaymentGatewayPaymentsTotal.Set(metrics.PaymentsTotal);
            BusinessKpiMetrics.PaymentGatewayVolumeTotal.Set(metrics.VolumeTotal);

            // Calculate average payment size
            var avgPaymentSize = metrics.PaymentsTotal > 0
                ? metrics.VolumeTotal / metrics.PaymentsTotal
                : 0;
            BusinessKpiMetrics.PaymentGatewayAvgPaymentSize.Set(avgPaymentSize);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect batched payment gateway metrics");
            BusinessKpiMetrics.CollectionErrors.WithLabels("payment_gateway_batched").Inc();
        }

        // Time-windowed metrics
        try
        {
            var gateways24h = await _repository.GetPaymentGatewaysCreatedAsync(Window24H, ct);
            var gateways7d = await _repository.GetPaymentGatewaysCreatedAsync(Window7D, ct);
            var gateways30d = await _repository.GetPaymentGatewaysCreatedAsync(Window30D, ct);

            BusinessKpiMetrics.PaymentGatewaysCreated.WithLabels("24h").Set(gateways24h);
            BusinessKpiMetrics.PaymentGatewaysCreated.WithLabels("7d").Set(gateways7d);
            BusinessKpiMetrics.PaymentGatewaysCreated.WithLabels("30d").Set(gateways30d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect payment gateways created metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("payment_gateways_created").Inc();
        }

        try
        {
            var payments24h = await _repository.GetPaymentGatewayPaymentsAsync(Window24H, ct);
            var payments7d = await _repository.GetPaymentGatewayPaymentsAsync(Window7D, ct);
            var payments30d = await _repository.GetPaymentGatewayPaymentsAsync(Window30D, ct);

            BusinessKpiMetrics.PaymentGatewayPayments.WithLabels("24h").Set(payments24h);
            BusinessKpiMetrics.PaymentGatewayPayments.WithLabels("7d").Set(payments7d);
            BusinessKpiMetrics.PaymentGatewayPayments.WithLabels("30d").Set(payments30d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect payment gateway payments metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("payment_gateway_payments").Inc();
        }

        try
        {
            var volume24h = await _repository.GetPaymentGatewayVolumeAsync(Window24H, ct);
            var volume7d = await _repository.GetPaymentGatewayVolumeAsync(Window7D, ct);
            var volume30d = await _repository.GetPaymentGatewayVolumeAsync(Window30D, ct);

            BusinessKpiMetrics.PaymentGatewayVolume.WithLabels("24h").Set(volume24h);
            BusinessKpiMetrics.PaymentGatewayVolume.WithLabels("7d").Set(volume7d);
            BusinessKpiMetrics.PaymentGatewayVolume.WithLabels("30d").Set(volume30d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect payment gateway volume metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("payment_gateway_volume").Inc();
        }

        try
        {
            var payers24h = await _repository.GetPaymentGatewayUniquePayersAsync(Window24H, ct);
            var payers7d = await _repository.GetPaymentGatewayUniquePayersAsync(Window7D, ct);
            var payers30d = await _repository.GetPaymentGatewayUniquePayersAsync(Window30D, ct);

            BusinessKpiMetrics.PaymentGatewayUniquePayers.WithLabels("24h").Set(payers24h);
            BusinessKpiMetrics.PaymentGatewayUniquePayers.WithLabels("7d").Set(payers7d);
            BusinessKpiMetrics.PaymentGatewayUniquePayers.WithLabels("30d").Set(payers30d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect payment gateway unique payers metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("payment_gateway_payers").Inc();
        }

        try
        {
            var payees24h = await _repository.GetPaymentGatewayUniquePayeesAsync(Window24H, ct);
            var payees7d = await _repository.GetPaymentGatewayUniquePayeesAsync(Window7D, ct);
            var payees30d = await _repository.GetPaymentGatewayUniquePayeesAsync(Window30D, ct);

            BusinessKpiMetrics.PaymentGatewayUniquePayees.WithLabels("24h").Set(payees24h);
            BusinessKpiMetrics.PaymentGatewayUniquePayees.WithLabels("7d").Set(payees7d);
            BusinessKpiMetrics.PaymentGatewayUniquePayees.WithLabels("30d").Set(payees30d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect payment gateway unique payees metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("payment_gateway_payees").Inc();
        }
    }

    // ============================================================================
    // Price & Ecosystem Value Metrics
    // ============================================================================

    private async Task CollectPriceAndEcosystemValueMetricsAsync(CancellationToken ct)
    {
        // Fetch GNO price and derive CRC price
        try
        {
            var (crcPriceUsd, crcPriceGno, source) = await _priceService.GetCrcPriceAsync(_lastTokenOfferPriceInCrc, ct);

            _lastCrcPriceUsd = crcPriceUsd;

            BusinessKpiMetrics.CrcPriceUsd.Set(crcPriceUsd);
            BusinessKpiMetrics.CrcPriceGno.Set(crcPriceGno);

            var (gnoPriceUsd, _) = await _priceService.GetGnoPriceUsdAsync(ct);
            BusinessKpiMetrics.GnoPriceUsd.Set(gnoPriceUsd);

            // Update price source indicator
            BusinessKpiMetrics.PriceSource.WithLabels("coingecko").Set(source == PriceService.PriceSource.CoinGeckoLive ? 1 : 0);
            BusinessKpiMetrics.PriceSource.WithLabels("cached").Set(source == PriceService.PriceSource.Cached ? 1 : 0);
            BusinessKpiMetrics.PriceSource.WithLabels("fallback").Set(source == PriceService.PriceSource.FallbackManual ? 1 : 0);
            BusinessKpiMetrics.PriceSource.WithLabels("balancer").Set(source == PriceService.PriceSource.BalancerLive ? 1 : 0);

            // Balancer market price (independent gauge, always set when available)
            if (_priceService.LastBalancerDcrcXdai > 0)
            {
                BusinessKpiMetrics.CrcPriceBalancerXdai.Set(_priceService.LastBalancerDcrcXdai);
                BusinessKpiMetrics.CrcPriceBalancerConvFactor.Set(
                    BalancerPriceService.GetConvFactor(DateTimeOffset.UtcNow));
            }

            if (_priceService.LastUpdate > DateTimeOffset.MinValue)
            {
                BusinessKpiMetrics.PriceLastUpdated.Set(_priceService.LastUpdate.ToUnixTimeSeconds());
            }

            _logger.LogDebug("Price metrics updated: CRC=${CrcUsd:F6}, GNO=${GnoUsd:F2}, Source={Source}",
                crcPriceUsd, gnoPriceUsd, source);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect price metrics");
            BusinessKpiMetrics.CollectionErrors.WithLabels("price_metrics").Inc();
        }

        // Calculate USD-denominated ecosystem values
        try
        {
            if (_lastCrcPriceUsd > 0)
            {
                // Total CRC supply in USD (use cached value from batched economic metrics)
                var totalSupply = await _repository.GetTotalCrcSupplyAsync(ct);
                BusinessKpiMetrics.TotalCrcSupplyUsd.Set(totalSupply * _lastCrcPriceUsd);

                // Daily mint volume in USD
                var dailyMintVolume = await _repository.GetDailyMintVolumeAsync(ct);
                BusinessKpiMetrics.DailyMintVolumeUsd.Set((double)dailyMintVolume * _lastCrcPriceUsd);

                // Daily transfer volume in USD
                var dailyTransferVolume = await _repository.GetDailyTransferVolumeAsync(ct);
                BusinessKpiMetrics.DailyTransferVolumeUsd.Set((double)dailyTransferVolume * _lastCrcPriceUsd);

                _logger.LogDebug("Ecosystem USD values: Supply=${Supply:F0}, DailyMint=${Mint:F2}, DailyTransfer=${Transfer:F2}",
                    totalSupply * _lastCrcPriceUsd,
                    (double)dailyMintVolume * _lastCrcPriceUsd,
                    (double)dailyTransferVolume * _lastCrcPriceUsd);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect ecosystem value metrics");
            BusinessKpiMetrics.CollectionErrors.WithLabels("ecosystem_value").Inc();
        }
    }
}
