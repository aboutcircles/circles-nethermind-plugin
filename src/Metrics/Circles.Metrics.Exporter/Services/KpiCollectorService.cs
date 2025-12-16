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
    private static readonly TimeSpan Window30D = TimeSpan.FromDays(30);

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
            CollectProfileMetricsAsync(ct)
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
}
