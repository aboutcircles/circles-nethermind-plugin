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
    private readonly Settings _settings = new();
    private readonly List<Exception> _getCurrentBlockErrors = new();
    private static readonly HttpClient HttpClient = new();

    // Prometheus metrics (static so there's only one set of counters/gauges regardless of service instantiation)
    private static readonly Counter GraphUpdatesCounter = Metrics.CreateCounter(
        "circles_graph_updates_total",
        "Number of times the background service updates the trust/balance graphs."
    );

    private static readonly Gauge LastProcessedBlockGauge = Metrics.CreateGauge(
        "circles_last_processed_block",
        "The most recent block number processed by the background service."
    );

    public NetworkStateUpdaterService(NetworkState networkState)
    {
        _networkState = networkState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Create two LoadGraph instances:
        // - Default (non-wrapped) with withWrap=false
        // - Wrapped with withWrap=true
        var loadGraphDefault = new LoadGraph(_settings.IndexReadonlyDbConnectionString);
        var loadGraphWrapped = new LoadGraph(_settings.IndexReadonlyDbConnectionString);
        var graphFactory = new GraphFactory();

        var lastBlock = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            lastBlock = await WaitForNextBlock(stoppingToken, lastBlock);
            LastProcessedBlockGauge.Set(lastBlock);

            // Load all four graphs in parallel
            var tasks = new List<Task>();

            // Task 1: Load default (non-wrapped) trust graph
            tasks.Add(Task.Run(() =>
            {
                // Pass null request to get full graph for network state
                var trustGraph = graphFactory.V2TrustGraph(loadGraphDefault);
                _networkState.Replace(trustGraph: trustGraph);
            }, stoppingToken));

            // Task 2: Load default (non-wrapped) balance graph
            tasks.Add(Task.Run(() =>
            {
                // Pass null request to get full graph for network state
                var balanceGraph = graphFactory.V2BalanceGraph(loadGraphDefault);
                _networkState.Replace(balanceGraph: balanceGraph);
            }, stoppingToken));

            // Task 3: Load wrapped trust graph
            tasks.Add(Task.Run(() =>
            {
                // Pass null request to get full graph for network state
                var trustGraph = graphFactory.V2TrustGraph(loadGraphWrapped);
                _networkState.Replace(wrappedTrustGraph: trustGraph);
            }, stoppingToken));

            // Task 4: Load wrapped balance graph
            tasks.Add(Task.Run(() =>
            {
                // Pass null request to get full graph for network state
                var balanceGraph = graphFactory.V2BalanceGraph(loadGraphWrapped);
                _networkState.Replace(wrappedBalanceGraph: balanceGraph);
            }, stoppingToken));

            await Task.WhenAll(tasks);

            GraphUpdatesCounter.Inc();
        }
    }

    private async Task<long> WaitForNextBlock(CancellationToken stoppingToken, long lastBlock)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var currentBlock = await GetBlockNumber();
            if (currentBlock <= lastBlock)
            {
                await Task.Delay(1000, stoppingToken);
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
                @params = new object[] { },
                id = 1
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            using var response = await HttpClient.PostAsync(_settings.CirclesRpcUrl, content);
            response.EnsureSuccessStatusCode();

            var rpcResponse = await response.Content.ReadFromJsonAsync<EthBlockNumberResponse>()
                              ?? throw new InvalidOperationException("Failed to deserialize Nethermind RPC response.");

            if (long.TryParse(rpcResponse.Result?.Replace("0x", ""),
                    System.Globalization.NumberStyles.HexNumber, null, out var blockNum))
            {
                _getCurrentBlockErrors.Clear();
                return blockNum;
            }

            return -1;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error getting block number: {e.Message}");
            _getCurrentBlockErrors.Add(e);

            if (_getCurrentBlockErrors.Count >= 20)
            {
                throw new AggregateException("Too many errors getting block number.", _getCurrentBlockErrors);
            }
        }

        return -1;
    }

    private class EthBlockNumberResponse
    {
        [JsonPropertyName("jsonrpc")] public string? JsonRpc { get; set; }
        [JsonPropertyName("result")] public string? Result { get; set; }
        [JsonPropertyName("id")] public int Id { get; set; }
    }
}