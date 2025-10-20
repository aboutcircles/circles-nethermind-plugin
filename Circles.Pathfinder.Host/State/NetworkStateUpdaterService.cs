using System.Diagnostics;
using System.Globalization;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Circles.Pathfinder.Host.State;

public class NetworkStateUpdaterService : BackgroundService
{
    private readonly NetworkState _networkState;
    private readonly Settings _settings = new();
    private readonly List<Exception> _getCurrentBlockErrors = new();
    private static readonly HttpClient HttpClient = new();
    private readonly ILogger<NetworkStateUpdaterService> _log;
    private readonly CapacityGraphPool _pool;

    public NetworkStateUpdaterService(NetworkState networkState,
        ILogger<NetworkStateUpdaterService> log,
        CapacityGraphPool pool)
    {
        _networkState = networkState;
        _log = log;
        _pool = pool;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loadGraph = new LoadGraph(_settings.IndexReadonlyDbConnectionString);
        var graphFactory = new GraphFactory();
        graphFactory.SetLoadGraph(loadGraph);

        _pool.SetLoadGraph(loadGraph);
        _pool.SetRouterAddress(_settings.RouterAddress);
        
        long lastBlock = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            _log.LogDebug("Waiting for next block…");
            lastBlock = await WaitForNextBlock(stoppingToken, lastBlock);
            _networkState.Replace(lastKnownBlockNumber: lastBlock);

            _log.LogDebug("→ got block {Block}", lastBlock);

            var swTotal = Stopwatch.StartNew();

            var swTrustGraph = Stopwatch.StartNew();
            var trustTask = Task.Run(() =>
            {
                var graph = graphFactory.V2TrustGraph(loadGraph);
                var lookup = GraphFactory.BuildTrustLookup(graph);

                _networkState.Replace(accountTrusts: lookup);
                swTrustGraph.Stop();
            }, stoppingToken);

            var swBalanceGraph = Stopwatch.StartNew();
            var balanceTask = Task.Run(() =>
            {
                var graph = graphFactory.V2BalanceGraph(loadGraph);
                _networkState.Replace(balanceGraph: graph);
                swBalanceGraph.Stop();
            }, stoppingToken);

            await Task.WhenAll(trustTask, balanceTask);
            swTotal.Stop();

            // Build full capacity graph with router address
            var cap = await CapacityGraphPool.BuildFullGraph(
                _networkState.BalanceGraph,
                _networkState.AccountTrusts,
                loadGraph,
                _settings.RouterAddress
            );
            var snap = new CapacityGraphSnapshot(lastBlock, cap);
            _pool.UpdateSnapshot(snap);

            _log.LogInformation(
                "Graphs updated – trust={TrustMs} ms balance={BalanceMs} ms total={TotalMs} ms",
                swTrustGraph.ElapsedMilliseconds,
                swBalanceGraph.ElapsedMilliseconds,
                swTotal.ElapsedMilliseconds);
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

            using var response = await HttpClient.PostAsync(_settings.CirclesRpcUrl, content);
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