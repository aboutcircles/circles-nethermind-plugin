using System.Diagnostics;
using System.Globalization;
using Circles.Pathfinder;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Prometheus;

namespace Circles.Pathfinder.Host.State;

public class NetworkStateUpdaterService : BackgroundService
{
    private readonly NetworkState _networkState;
    private readonly Settings _settings;
    private readonly List<Exception> _getCurrentBlockErrors = new();
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private readonly ILogger<NetworkStateUpdaterService> _log;
    private readonly CapacityGraphPool _pool;

    public NetworkStateUpdaterService(NetworkState networkState,
        Circles.Pathfinder.Host.Settings settings,
        ILogger<NetworkStateUpdaterService> log,
        CapacityGraphPool pool)
    {
        _networkState = networkState;
        _settings = settings;
        _log = log;
        _pool = pool;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loadGraph = new LoadGraph(_settings);
        var graphFactory = new GraphFactory(_settings.BaseGroupRouter, loadGraph);

        long lastBlock = _networkState.LastKnownBlockNumber;
        int consecutiveErrors = 0;
        const int maxConsecutiveErrors = 5;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _log.LogDebug("Waiting for next block…");
                lastBlock = await WaitForNextBlock(stoppingToken, lastBlock);
                _networkState.Replace(lastKnownBlockNumber: lastBlock);

                _log.LogDebug("→ got block {Block}", lastBlock);

                var swTotal = Stopwatch.StartNew();

                var swTrustGraph = Stopwatch.StartNew();
                var trustTask = Task.Run(() =>
                {
                    try
                    {
                        var graph = graphFactory.V2TrustGraph();
                        var lookup = GraphFactory.BuildTrustLookup(graph);
                        _networkState.Replace(accountTrusts: lookup);
                        swTrustGraph.Stop();
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Error loading trust graph");
                        swTrustGraph.Stop();
                        throw; // Re-throw to be handled by outer try-catch
                    }
                }, stoppingToken);

                var swBalanceGraph = Stopwatch.StartNew();
                var balanceTask = Task.Run(() =>
                {
                    try
                    {
                        var graph = graphFactory.V2BalanceGraph();
                        _networkState.Replace(balanceGraph: graph);
                        swBalanceGraph.Stop();
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Error loading balance graph");
                        swBalanceGraph.Stop();
                        throw; // Re-throw to be handled by outer try-catch
                    }
                }, stoppingToken);

                await Task.WhenAll(trustTask, balanceTask);
                swTotal.Stop();

                // Build full capacity graph with router address
                var cap = await CapacityGraphPool.BuildFullGraph(
                    _networkState.BalanceGraph ?? throw new InvalidOperationException("Balance graph is null"),
                    _networkState.AccountTrusts ?? throw new InvalidOperationException("Account trusts is null"),
                    loadGraph,
                    _settings.BaseGroupRouter
                );
                // Extract group/consent data for caching (avoids 3 DB queries per filtered request)
                var groupData = new CachedGroupData(
                    new HashSet<int>(cap.GroupNodes),
                    cap.GroupTrustedTokens.ToDictionary(kv => kv.Key, kv => new HashSet<int>(kv.Value)),
                    new HashSet<int>(cap.ConsentedAvatars));

                var snap = new CapacityGraphSnapshot(lastBlock, cap);
                _pool.UpdateSnapshot(snap, groupData);

                _log.LogInformation(
                    "Graphs updated – trust={TrustMs} ms balance={BalanceMs} ms total={TotalMs} ms",
                    swTrustGraph.ElapsedMilliseconds,
                    swBalanceGraph.ElapsedMilliseconds,
                    swTotal.ElapsedMilliseconds);

                // Record metrics for successful update
                GraphUpdateMetrics.UpdateDuration.WithLabels("trust").Observe(swTrustGraph.Elapsed.TotalSeconds);
                GraphUpdateMetrics.UpdateDuration.WithLabels("balance").Observe(swBalanceGraph.Elapsed.TotalSeconds);
                GraphUpdateMetrics.UpdateDuration.WithLabels("total").Observe(swTotal.Elapsed.TotalSeconds);
                GraphUpdateMetrics.UpdateTotal.WithLabels("success").Inc();
                GraphUpdateMetrics.LastUpdateTimestamp.Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                GraphUpdateMetrics.LastProcessedBlock.Set(lastBlock);
                GraphUpdateMetrics.ConsecutiveErrors.Set(0);

                // O3: Graph size gauges
                var bg = _networkState.BalanceGraph;
                if (bg != null)
                {
                    GraphUpdateMetrics.AvatarCount.Set(bg.AvatarNodes.Count);
                    GraphUpdateMetrics.BalanceCount.Set(bg.BalanceNodes.Count);
                }
                GraphUpdateMetrics.EdgeCount.Set(cap.Edges.Count);
                GraphUpdateMetrics.GroupCount.Set(cap.GroupNodes.Count);
                GraphUpdateMetrics.ConsentedAvatarCount.Set(cap.ConsentedAvatars.Count);

                // O9: Address pool size
                GraphUpdateMetrics.AddressPoolSize.Set(AddressIdPool.Count);

                // Reset error counter on success
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException)
            {
                _log.LogInformation("NetworkStateUpdaterService stopping due to cancellation");
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                GraphUpdateMetrics.UpdateTotal.WithLabels("failure").Inc();
                GraphUpdateMetrics.ConsecutiveErrors.Set(consecutiveErrors);
                _log.LogError(ex, "Error updating network state (attempt {Attempt}/{MaxAttempts})", consecutiveErrors, maxConsecutiveErrors);

                if (consecutiveErrors >= maxConsecutiveErrors)
                {
                    _log.LogCritical("Too many consecutive errors ({Count}), crashing service to trigger container restart", consecutiveErrors);
                    throw new InvalidOperationException(
                        $"NetworkStateUpdaterService unrecoverable after {consecutiveErrors} consecutive failures. " +
                        $"Last error: {ex.Message}");
                }

                // Wait before retrying with exponential backoff
                var retryDelay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, consecutiveErrors)));
                _log.LogInformation("Retrying in {Delay} seconds", retryDelay.TotalSeconds);

                try
                {
                    await Task.Delay(retryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<long> WaitForNextBlock(CancellationToken stoppingToken, long lastBlock)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            long currentBlock = await GetBlockNumber();
            if (currentBlock <= lastBlock)
            {
                await Task.Delay(1_000, stoppingToken);
            }
            else
            {
                return currentBlock;
            }
        }

        return lastBlock;
    }

    private async Task<long> GetBlockNumber()
    {
        try
        {
            var requestBody = new
            {
                jsonrpc = "2.0",
                method = "eth_blockNumber",
                @params = Array.Empty<object>(),
                id = 1
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            using var response = await HttpClient.PostAsync(_settings.NethermindRpcUrl, content);
            response.EnsureSuccessStatusCode();

            var rpcResponse = await response.Content.ReadFromJsonAsync<EthBlockNumberResponse>()
                              ?? throw new InvalidOperationException("Failed to deserialize Nethermind RPC response.");

            if (long.TryParse(rpcResponse.Result?.Replace("0x", ""),
                    NumberStyles.HexNumber, null, out var num))
            {
                _getCurrentBlockErrors.Clear();
                return num;
            }

            return -1;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Error getting block number");
            _getCurrentBlockErrors.Add(e);

            if (_getCurrentBlockErrors.Count >= Constants.MaxGetBlockErrors)
            {
                var errors = new List<Exception>(_getCurrentBlockErrors);
                _getCurrentBlockErrors.Clear();
                throw new AggregateException("Too many errors getting block number.", errors);
            }
        }

        return -1;
    }

    private sealed class EthBlockNumberResponse
    {
        [JsonPropertyName("jsonrpc")] public string? JsonRpc { get; set; }
        [JsonPropertyName("result")] public string? Result { get; set; }
        [JsonPropertyName("id")] public int Id { get; set; }
    }
}