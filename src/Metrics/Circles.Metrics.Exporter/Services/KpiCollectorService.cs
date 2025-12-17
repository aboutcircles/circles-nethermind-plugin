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
    private static readonly TimeSpan Window180D = TimeSpan.FromDays(180);
    private static readonly TimeSpan Window1Y = TimeSpan.FromDays(365);

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
            CollectNetworkHealthMetricsAsync(ct),
            CollectAdvancedMonetaryMetricsAsync(ct)
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
    // Advanced Monetary/Economic Metrics
    // ============================================================================

    private async Task CollectAdvancedMonetaryMetricsAsync(CancellationToken ct)
    {
        // Total CRC supply
        try
        {
            var supply = await _repository.GetTotalCrcSupplyAsync(ct);
            BusinessKpiMetrics.TotalCrcSupply.Set(supply);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect total CRC supply metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("total_crc_supply").Inc();
        }

        // Total minted all time
        try
        {
            var totalMinted = await _repository.GetTotalMintedAllTimeAsync(ct);
            BusinessKpiMetrics.TotalMintedAllTime.Set(totalMinted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect total minted all time metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("total_minted_all_time").Inc();
        }

        // Demurrage paid by window
        try
        {
            var dem24h = await _repository.GetDemurragePaidAsync(Window24H, ct);
            var dem7d = await _repository.GetDemurragePaidAsync(Window7D, ct);
            var dem30d = await _repository.GetDemurragePaidAsync(Window30D, ct);

            BusinessKpiMetrics.DemurragePaid.WithLabels("24h").Set(dem24h);
            BusinessKpiMetrics.DemurragePaid.WithLabels("7d").Set(dem7d);
            BusinessKpiMetrics.DemurragePaid.WithLabels("30d").Set(dem30d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect demurrage paid metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("demurrage_paid").Inc();
        }

        // Money velocity by window
        try
        {
            var vel7d = await _repository.GetMoneyVelocityAsync(Window7D, ct);
            var vel30d = await _repository.GetMoneyVelocityAsync(Window30D, ct);
            var vel90d = await _repository.GetMoneyVelocityAsync(Window90D, ct);
            var vel180d = await _repository.GetMoneyVelocityAsync(Window180D, ct);
            var vel1y = await _repository.GetMoneyVelocityAsync(Window1Y, ct);

            BusinessKpiMetrics.MoneyVelocity.WithLabels("7d").Set(vel7d);
            BusinessKpiMetrics.MoneyVelocity.WithLabels("30d").Set(vel30d);
            BusinessKpiMetrics.MoneyVelocity.WithLabels("90d").Set(vel90d);
            BusinessKpiMetrics.MoneyVelocity.WithLabels("180d").Set(vel180d);
            BusinessKpiMetrics.MoneyVelocity.WithLabels("1y").Set(vel1y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect money velocity metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("money_velocity").Inc();
        }

        // Active balance holders
        try
        {
            var holders = await _repository.GetActiveBalanceHoldersAsync(ct);
            BusinessKpiMetrics.ActiveBalanceHolders.Set(holders);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect active balance holders metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("active_balance_holders").Inc();
        }

        // Average and median balance
        try
        {
            var avgBalance = await _repository.GetAverageBalanceAsync(ct);
            var medianBalance = await _repository.GetMedianBalanceAsync(ct);

            BusinessKpiMetrics.AverageBalance.Set(avgBalance);
            BusinessKpiMetrics.MedianBalance.Set(medianBalance);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect balance distribution metrics");
            BusinessKpiMetrics.CollectionErrors.WithLabels("balance_distribution").Inc();
        }

        // Gini coefficient (wealth equality)
        try
        {
            var gini = await _repository.GetGiniCoefficientAsync(ct);
            BusinessKpiMetrics.GiniCoefficient.Set(gini);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect Gini coefficient metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("gini_coefficient").Inc();
        }

        // Top holder concentration
        try
        {
            var top10 = await _repository.GetTopHolderConcentrationAsync(10, ct);
            var top100 = await _repository.GetTopHolderConcentrationAsync(100, ct);
            var top1000 = await _repository.GetTopHolderConcentrationAsync(1000, ct);

            BusinessKpiMetrics.TopHolderConcentration.WithLabels("10").Set(top10);
            BusinessKpiMetrics.TopHolderConcentration.WithLabels("100").Set(top100);
            BusinessKpiMetrics.TopHolderConcentration.WithLabels("1000").Set(top1000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect top holder concentration metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("top_holder_concentration").Inc();
        }

        // Daily/Weekly/Monthly active wallets (DAW/WAW/MAW)
        try
        {
            var daw = await _repository.GetDailyActiveWalletsAsync(ct);
            var waw = await _repository.GetWeeklyActiveWalletsAsync(ct);
            var maw = await _repository.GetMonthlyActiveWalletsAsync(ct);

            BusinessKpiMetrics.DailyActiveWallets.Set(daw);
            BusinessKpiMetrics.WeeklyActiveWallets.Set(waw);
            BusinessKpiMetrics.MonthlyActiveWallets.Set(maw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect active wallets metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("active_wallets").Inc();
        }

        // User retention rate
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

        // First-time transactors
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

        // Transfer size distribution (percentiles)
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

        // Micro and large transaction counts
        try
        {
            // Micro transactions (< 1 CRC)
            var micro24h = await _repository.GetMicroTransactionCountAsync(Window24H, 1.0, ct);
            var micro7d = await _repository.GetMicroTransactionCountAsync(Window7D, 1.0, ct);

            BusinessKpiMetrics.MicroTransactionCount.WithLabels("24h").Set(micro24h);
            BusinessKpiMetrics.MicroTransactionCount.WithLabels("7d").Set(micro7d);

            // Large transactions (> 100 CRC)
            var large24h = await _repository.GetLargeTransactionCountAsync(Window24H, 100.0, ct);
            var large7d = await _repository.GetLargeTransactionCountAsync(Window7D, 100.0, ct);

            BusinessKpiMetrics.LargeTransactionCount.WithLabels("24h").Set(large24h);
            BusinessKpiMetrics.LargeTransactionCount.WithLabels("7d").Set(large7d);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect transaction size bucket metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("transaction_size_buckets").Inc();
        }

        // Net inflow (minting minus demurrage approximation)
        try
        {
            var inflow24h = await _repository.GetNetInflowAsync(Window24H, ct);
            var inflow7d = await _repository.GetNetInflowAsync(Window7D, ct);
            var inflow30d = await _repository.GetNetInflowAsync(Window30D, ct);
            var inflow90d = await _repository.GetNetInflowAsync(Window90D, ct);
            var inflow180d = await _repository.GetNetInflowAsync(Window180D, ct);
            var inflow1y = await _repository.GetNetInflowAsync(Window1Y, ct);

            BusinessKpiMetrics.NetInflow.WithLabels("24h").Set(inflow24h);
            BusinessKpiMetrics.NetInflow.WithLabels("7d").Set(inflow7d);
            BusinessKpiMetrics.NetInflow.WithLabels("30d").Set(inflow30d);
            BusinessKpiMetrics.NetInflow.WithLabels("90d").Set(inflow90d);
            BusinessKpiMetrics.NetInflow.WithLabels("180d").Set(inflow180d);
            BusinessKpiMetrics.NetInflow.WithLabels("1y").Set(inflow1y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect net inflow metric");
            BusinessKpiMetrics.CollectionErrors.WithLabels("net_inflow").Inc();
        }
    }
}
