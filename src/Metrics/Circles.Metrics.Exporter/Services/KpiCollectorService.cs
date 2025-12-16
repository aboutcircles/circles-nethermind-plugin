using System.Diagnostics;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Background service that periodically collects Circles KPIs and updates Prometheus metrics.
/// </summary>
public class KpiCollectorService : BackgroundService
{
    private readonly KpiRepository _repository;
    private readonly ILogger<KpiCollectorService> _logger;
    private readonly TimeSpan _collectionInterval;

    // Time windows for metrics
    private static readonly TimeSpan Window24H = TimeSpan.FromHours(24);
    private static readonly TimeSpan Window7D = TimeSpan.FromDays(7);
    private static readonly TimeSpan Window14D = TimeSpan.FromDays(14);
    private static readonly TimeSpan Window30D = TimeSpan.FromDays(30);
    private static readonly TimeSpan Window90D = TimeSpan.FromDays(90);

    public KpiCollectorService(
        KpiRepository repository,
        ILogger<KpiCollectorService> logger,
        IConfiguration configuration)
    {
        _repository = repository;
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
        // Collect all KPIs in parallel for efficiency
        await Task.WhenAll(
            CollectUserMetricsAsync(ct),
            CollectTrustMetricsAsync(ct),
            CollectEconomicMetricsAsync(ct),
            CollectGroupMetricsAsync(ct),
            CollectProfileMetricsAsync(ct),
            CollectDuneParityMetricsAsync(ct),
            CollectActivityRatesAsync(ct),
            CollectSybilDetectionMetricsAsync(ct),
            CollectNetworkHealthMetricsAsync(ct)
        );
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

            BusinessKpiMetrics.NewUsers.WithLabels("24h").Set(newUsers24h);
            BusinessKpiMetrics.NewUsers.WithLabels("7d").Set(newUsers7d);
            BusinessKpiMetrics.NewUsers.WithLabels("30d").Set(newUsers30d);
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
            BusinessKpiMetrics.ActiveMinters.WithLabels("24h").Set(minters24h);
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

            BusinessKpiMetrics.NewBackers.WithLabels("24h").Set(newBackers24h);
            BusinessKpiMetrics.NewBackers.WithLabels("7d").Set(newBackers7d);
            BusinessKpiMetrics.NewBackers.WithLabels("30d").Set(newBackers30d);
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

            BusinessKpiMetrics.NewOrganizations.WithLabels("24h").Set(newOrgs24h);
            BusinessKpiMetrics.NewOrganizations.WithLabels("7d").Set(newOrgs7d);
            BusinessKpiMetrics.NewOrganizations.WithLabels("30d").Set(newOrgs30d);
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

            BusinessKpiMetrics.NewGroups.WithLabels("24h").Set(newGroups24h);
            BusinessKpiMetrics.NewGroups.WithLabels("7d").Set(newGroups7d);
            BusinessKpiMetrics.NewGroups.WithLabels("30d").Set(newGroups30d);
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

            BusinessKpiMetrics.MintingRate.WithLabels("14d").Set(rate14d);
            BusinessKpiMetrics.MintingRate.WithLabels("30d").Set(rate30d);
            BusinessKpiMetrics.MintingRate.WithLabels("90d").Set(rate90d);
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

            BusinessKpiMetrics.SpendingRate.WithLabels("14d").Set(rate14d);
            BusinessKpiMetrics.SpendingRate.WithLabels("30d").Set(rate30d);
            BusinessKpiMetrics.SpendingRate.WithLabels("90d").Set(rate90d);
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

            BusinessKpiMetrics.TransferVolume.WithLabels("7d").Set(vol7d);
            BusinessKpiMetrics.TransferVolume.WithLabels("30d").Set(vol30d);
            BusinessKpiMetrics.TransferVolume.WithLabels("90d").Set(vol90d);
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

            BusinessKpiMetrics.TransferCount.WithLabels("24h").Set(count24h);
            BusinessKpiMetrics.TransferCount.WithLabels("7d").Set(count7d);
            BusinessKpiMetrics.TransferCount.WithLabels("30d").Set(count30d);
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
}
