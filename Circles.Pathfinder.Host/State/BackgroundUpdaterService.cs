using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Circles.Pathfinder.Host.State;

public sealed class NetworkState
{
    public TrustGraph? TrustGraph => _trustGraph;
    private TrustGraph? _trustGraph;

    public BalanceGraph? BalanceGraph => _balanceGraph;
    private BalanceGraph? _balanceGraph;

    internal void Replace(TrustGraph? trustGraph, BalanceGraph? balanceGraph)
    {
        Interlocked.Exchange(ref _trustGraph, trustGraph);
        Interlocked.Exchange(ref _balanceGraph, balanceGraph);
    }
}

public class BackgroundUpdaterService(NetworkState networkState) : BackgroundService
{
    private readonly Settings _settings = new();

    private static readonly HttpClient HttpClient = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loadGraph = new LoadGraph(_settings.IndexReadonlyDbConnectionString);
        var graphFactory = new GraphFactory();

        var lastBlock = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            lastBlock = await WaitForNextBlock(stoppingToken, lastBlock);

            Console.WriteLine("Updating graphs...");

            var p1 = Task.Run(() =>
            {
                var trustGraph = graphFactory.V2TrustGraph(loadGraph);
                networkState.Replace(trustGraph, networkState.BalanceGraph);
            }, stoppingToken);

            var p2 = Task.Run(() =>
            {
                var balanceGraph = graphFactory.V2BalanceGraph(loadGraph);
                networkState.Replace(networkState.TrustGraph, balanceGraph);
            }, stoppingToken);

            await Task.WhenAll(p1, p2);

            Console.WriteLine("Graphs updated.");
        }
    }

    private async Task<long> WaitForNextBlock(CancellationToken stoppingToken, long lastBlock)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var currentBlock = await GetBlockNumber();
            if (currentBlock <= lastBlock)
            {
                Console.WriteLine($"Waiting for new block. Current block: {currentBlock}");
                await Task.Delay(1000, stoppingToken);
            }
            else
            {
                Console.WriteLine($"New block: {currentBlock}");
                return currentBlock;
            }
        }

        // If we exit the loop because of cancellation, just return whatever we had
        return lastBlock;
    }

    private async Task<long> GetBlockNumber()
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

        if (long.TryParse(rpcResponse.Result?.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null,
                out var blockNum))
        {
            return blockNum;
        }

        throw new InvalidOperationException("Failed to parse block number from Nethermind RPC response.");
    }

    private class EthBlockNumberResponse
    {
        [JsonPropertyName("jsonrpc")] public string? JsonRpc { get; set; }
        [JsonPropertyName("result")] public string? Result { get; set; }
        [JsonPropertyName("id")] public int Id { get; set; }
    }
}