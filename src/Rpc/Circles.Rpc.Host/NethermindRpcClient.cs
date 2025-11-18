using System.Text.Json;

namespace Circles.Rpc.Host;

/// <summary>
/// Client for making JSON-RPC calls to Nethermind for eth_call operations.
/// </summary>
public class NethermindRpcClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _rpcUrl;

    public NethermindRpcClient(IHttpClientFactory httpClientFactory, string rpcUrl)
    {
        _httpClientFactory = httpClientFactory;
        _rpcUrl = rpcUrl;
    }

    /// <summary>
    /// Makes an eth_call to Nethermind.
    /// </summary>
    public async Task<string> EthCall(string to, string data, string? block = "latest")
    {
        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        var request = new
        {
            jsonrpc = "2.0",
            method = "eth_call",
            @params = new object[]
            {
                new
                {
                    to,
                    data
                },
                block
            },
            id = 1
        };

        var response = await httpClient.PostAsJsonAsync(_rpcUrl, request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var dataStr = result.GetProperty("result").GetString();

        if (dataStr == null || !dataStr.StartsWith("0x"))
        {
            throw new Exception("Invalid eth_call response");
        }

        return dataStr;
    }

    /// <summary>
    /// Checks if Nethermind is syncing by calling eth_syncing.
    /// Returns true if not syncing (fully synced), false if syncing.
    /// </summary>
    public async Task<bool> IsSynced()
    {
        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        var request = new
        {
            jsonrpc = "2.0",
            method = "eth_syncing",
            @params = Array.Empty<object>(),
            id = 1
        };

        var response = await httpClient.PostAsJsonAsync(_rpcUrl, request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var syncingResult = result.GetProperty("result");

        // eth_syncing returns false if not syncing, or an object if syncing
        return syncingResult.ValueKind == JsonValueKind.False;
    }

    /// <summary>
    /// Gets the latest block number from Nethermind.
    /// </summary>
    public async Task<long> GetLatestBlockNumber()
    {
        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        var request = new
        {
            jsonrpc = "2.0",
            method = "eth_blockNumber",
            @params = Array.Empty<object>(),
            id = 1
        };

        var response = await httpClient.PostAsJsonAsync(_rpcUrl, request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var blockNumberHex = result.GetProperty("result").GetString();

        if (blockNumberHex == null || !blockNumberHex.StartsWith("0x"))
        {
            throw new Exception("Invalid eth_blockNumber response");
        }

        return Convert.ToInt64(blockNumberHex, 16);
    }
}
