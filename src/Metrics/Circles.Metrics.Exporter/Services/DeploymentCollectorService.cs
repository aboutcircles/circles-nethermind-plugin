using System.Diagnostics;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Background service that periodically probes multiple Circles RPC endpoints
/// to determine deployment status across environments.
///
/// Configuration (appsettings.json or env vars):
///   "Deployment": {
///     "Environments": {
///       "staging2": "https://staging.circlesubi.network",
///       "prod": "https://rpc.aboutcircles.com"
///     }
///   }
///
/// Env var: Deployment__Environments__prod=https://rpc.aboutcircles.com
/// </summary>
public class DeploymentCollectorService : BackgroundService
{
    private readonly DeploymentProber _prober;
    private readonly ILogger<DeploymentCollectorService> _logger;
    private readonly TimeSpan _collectionInterval;
    private readonly Dictionary<string, string> _environments;

    public DeploymentCollectorService(
        DeploymentProber prober,
        ILogger<DeploymentCollectorService> logger,
        IConfiguration configuration)
    {
        _prober = prober;
        _logger = logger;

        var intervalSeconds = configuration.GetValue<int>("Metrics:DeploymentCollectionIntervalSeconds", 300);
        _collectionInterval = TimeSpan.FromSeconds(intervalSeconds);

        _environments = new Dictionary<string, string>();
        var envSection = configuration.GetSection("Deployment:Environments");
        foreach (var child in envSection.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                _environments[child.Key] = child.Value;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_environments.Count == 0)
        {
            _logger.LogWarning("Deployment Collector: no environments configured under Deployment:Environments, skipping");
            return;
        }

        _logger.LogInformation(
            "Deployment Collector starting with {Interval}s interval, probing {Count} environments: {Environments}",
            _collectionInterval.TotalSeconds,
            _environments.Count,
            string.Join(", ", _environments.Select(e => $"{e.Key}={e.Value}")));

        // Stagger after KPI (5s), Trust (30s), Liquidity (60s)
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);

        var expectedTotal = DeploymentProber.ExpectedTables.Values.Sum(v => v.Length);
        DeploymentMetrics.ExpectedTableCount.Set(expectedTotal);

        while (!stoppingToken.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                // Probe all environments in parallel
                var tasks = _environments.Select(env =>
                    CollectEnvironmentAsync(env.Key, env.Value, stoppingToken));
                await Task.WhenAll(tasks);

                sw.Stop();
                DeploymentMetrics.CollectionDuration.Inc(sw.Elapsed.TotalSeconds);

                _logger.LogDebug("Deployment probe completed in {Duration}ms for {Count} environments",
                    sw.ElapsedMilliseconds, _environments.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during deployment probe");
            }

            await Task.Delay(_collectionInterval, stoppingToken);
        }
    }

    private async Task CollectEnvironmentAsync(string environment, string rpcUrl, CancellationToken ct)
    {
        try
        {
            var result = await _prober.ProbeEnvironmentAsync(rpcUrl, environment, ct);

            if (result == null)
            {
                // RPC unreachable
                DeploymentMetrics.EnvironmentReachable.WithLabels(environment).Set(0);
                _logger.LogWarning("[{Environment}] RPC unreachable at {Url}", environment, rpcUrl);
                DeploymentMetrics.CollectionErrors.WithLabels(environment, "rpc_unreachable").Inc();
                return;
            }

            DeploymentMetrics.EnvironmentReachable.WithLabels(environment).Set(1);
            DeploymentMetrics.SchemaTableCount.WithLabels(environment).Set(result.Value.SchemaCount);

            int existingCount = 0;
            int missingCount = 0;

            foreach (var status in result.Value.Statuses)
            {
                DeploymentMetrics.TableExists
                    .WithLabels(environment, status.Namespace, status.FullTableName)
                    .Set(status.Exists ? 1 : 0);

                if (status.Exists)
                    existingCount++;
                else
                    missingCount++;
            }

            DeploymentMetrics.ExistingTableCount.WithLabels(environment).Set(existingCount);
            DeploymentMetrics.MissingTableCount.WithLabels(environment).Set(missingCount);
            DeploymentMetrics.LastCollectionTimestamp.WithLabels(environment)
                .Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            if (missingCount > 0)
            {
                var missing = result.Value.Statuses.Where(s => !s.Exists).Select(s => s.FullTableName);
                _logger.LogWarning("[{Environment}] Missing/empty {Count} tables: {Tables}",
                    environment, missingCount, string.Join(", ", missing));
            }
            else
            {
                _logger.LogInformation("[{Environment}] All {Count} expected tables present", environment, existingCount);
            }
        }
        catch (Exception ex)
        {
            DeploymentMetrics.EnvironmentReachable.WithLabels(environment).Set(0);
            _logger.LogWarning(ex, "[{Environment}] Failed to probe deployment status at {Url}", environment, rpcUrl);
            DeploymentMetrics.CollectionErrors.WithLabels(environment, "probe_failed").Inc();
        }
    }
}
