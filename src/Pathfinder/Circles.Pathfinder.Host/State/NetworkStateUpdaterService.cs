using System.Diagnostics;
using System.Globalization;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Index.Common;

namespace Circles.Pathfinder.Host.State;

public class NetworkStateUpdaterService : BackgroundService
{
    private readonly NetworkState _networkState;
    private readonly Circles.Pathfinder.Host.Settings _settings;
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

        long lastBlock = 0;
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
                var trustTask = Task.Run(async () =>
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
                var balanceTask = Task.Run(async () =>
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
                    _networkState.BalanceGraph,
                    _networkState.AccountTrusts,
                    loadGraph,
                    _settings.BaseGroupRouter
                );
                var snap = new CapacityGraphSnapshot(lastBlock, cap);
                _pool.UpdateSnapshot(snap);

                _log.LogInformation(
                    "Graphs updated – trust={TrustMs} ms balance={BalanceMs} ms total={TotalMs} ms",
                    swTrustGraph.ElapsedMilliseconds,
                    swBalanceGraph.ElapsedMilliseconds,
                    swTotal.ElapsedMilliseconds);

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
                _log.LogError(ex, "Error updating network state (attempt {Attempt}/{MaxAttempts})", consecutiveErrors, maxConsecutiveErrors);

                if (consecutiveErrors >= maxConsecutiveErrors)
                {
                    _log.LogCritical("Too many consecutive errors ({Count}), giving up", consecutiveErrors);
                    break;
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
                throw new AggregateException("Too many errors getting block number.", _getCurrentBlockErrors);
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