using System.Net;
using System.Text;
using System.Text.Json;
using Circles.Common.Dto;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Nethermind.Int256;
using System.Text.Json.Serialization;
using Circles.Common;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Simple test case model for pathfinder tests
/// </summary>
public class PathfinderTestCase
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("description")] public string Description { get; set; } = "";

    [JsonPropertyName("source")] public string Source { get; set; } = "";

    [JsonPropertyName("sink")] public string Sink { get; set; } = "";

    [JsonPropertyName("targetFlow")] public string TargetFlow { get; set; } = "";

    [JsonPropertyName("fromTokens")] public string[] FromTokens { get; set; } = Array.Empty<string>();

    [JsonPropertyName("toTokens")] public string[] ToTokens { get; set; } = Array.Empty<string>();

    [JsonPropertyName("excludedFromTokens")]
    public string[] ExcludedFromTokens { get; set; } = Array.Empty<string>();

    [JsonPropertyName("excludedToTokens")] public string[] ExcludedToTokens { get; set; } = Array.Empty<string>();

    [JsonPropertyName("withWrap")] public bool WithWrap { get; set; } = false;
}

/// <summary>
/// NetworkSnapshot model matching the /snapshot endpoint response
/// </summary>
public class NetworkSnapshotResponse
{
    [JsonPropertyName("blockNumber")] public long BlockNumber { get; set; }

    [JsonPropertyName("addresses")] public List<string> Addresses { get; set; } = new();

    [JsonPropertyName("trust")] public Dictionary<int, HashSet<int>> Trust { get; set; } = new();

    [JsonPropertyName("balance")] public Dictionary<int, List<BalanceNodeJson>> Balance { get; set; } = new();
}

/// <summary>
/// BalanceNode representation in JSON
/// </summary>
public class BalanceNodeJson
{
    [JsonPropertyName("address")] public int Address { get; set; }

    [JsonPropertyName("holder")] public int Holder { get; set; }

    [JsonPropertyName("token")] public int Token { get; set; }

    [JsonPropertyName("amount")] public long Amount { get; set; }

    [JsonPropertyName("isWrapped")] public bool IsWrapped { get; set; }

    [JsonPropertyName("isStatic")] public bool IsStatic { get; set; }
}

/// <summary>
/// Tests for the pathfinder service using real network data via HTTP requests.
/// These tests ensure the pathfinder respects flow conservation, token filters, and other integrity rules.
/// </summary>
[TestFixture]
public class NetworkPathfinderTests
{
    private static readonly bool NetworkTestsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("RUN_PATHFINDER_NETWORK_TESTS"), "true",
            StringComparison.OrdinalIgnoreCase);

    private readonly string _pathfinderBaseUrl;
    private readonly string _rpcUrl;
    private HttpClient? _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    // Graphs for validating trust and balance
    private TrustGraph? _trustGraph;
    private BalanceGraph? _balanceGraph;
    private GraphFactory? _graphFactory;
    private bool _graphsLoaded = false;

    // Router and Groups - loaded dynamically from database
    private readonly string _routerAddress;
    private HashSet<string> _dynamicGroups = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, HashSet<string>> _groupTrustedTokens = new(StringComparer.OrdinalIgnoreCase);

    // ANSI color escape sequences for console output
    private static class ConsoleColors
    {
        public const string Reset = "\u001b[0m";
        public const string Red = "\u001b[31m";
        public const string Green = "\u001b[32m";
        public const string Yellow = "\u001b[33m";
        public const string Blue = "\u001b[34m";
        public const string Magenta = "\u001b[35m";
        public const string Cyan = "\u001b[36m";
    }

    public NetworkPathfinderTests()
    {
        // Set the base URL for your Pathfinder service
        _pathfinderBaseUrl = Environment.GetEnvironmentVariable("PATHFINDER_URL") ?? "http://localhost:8080";
        _rpcUrl = Environment.GetEnvironmentVariable("RPC_URL") ?? "http://localhost:8081";

        // Get router address from environment or use default
        _routerAddress = Environment.GetEnvironmentVariable("ROUTER_ADDRESS")
                         ?? "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!NetworkTestsEnabled)
        {
            Assert.Ignore("Skipping NetworkPathfinderTests because RUN_PATHFINDER_NETWORK_TESTS is not set to 'true'.");
        }

        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30); // Set reasonable timeout
        Console.WriteLine(
            $"{ConsoleColors.Cyan}Connecting to Pathfinder service at: {_pathfinderBaseUrl}{ConsoleColors.Reset}");

        // Load the graph data
        LoadGraphs();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        // Properly dispose the HttpClient
        _httpClient?.Dispose();
    }

    /// <summary>
    /// Loads trust and balance graphs from the database
    /// </summary>
    private void LoadGraphs()
    {
        var settings = new TestSettings();

        // First try to load from snapshot endpoint
        try
        {
            Console.WriteLine(
                $"{ConsoleColors.Cyan}Loading network graphs from pathfinder snapshot...{ConsoleColors.Reset}");

            var loadTask = LoadGraphsFromSnapshot();
            loadTask.Wait(); // Wait synchronously since we're in a non-async method

            if (_graphsLoaded)
            {
                return; // Successfully loaded from snapshot
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"{ConsoleColors.Yellow}Could not load from snapshot: {ex.Message}{ConsoleColors.Reset}");
        }

        // Fall back to original database loading method
        // Get connection string from environment variable
        if (string.IsNullOrEmpty(settings.IndexReadonlyDbConnectionString))
        {
            Console.WriteLine(
                $"{ConsoleColors.Red}Warning: POSTGRES_READONLY_CONNECTION_STRING environment variable is not set.{ConsoleColors.Reset}");
            Console.WriteLine(
                "Trust and balance validation will be skipped. Set this environment variable to enable validation.");
            return;
        }

        try
        {
            Console.WriteLine($"{ConsoleColors.Cyan}Loading network graphs from database...{ConsoleColors.Reset}");

            var loadGraph = new LoadGraph(settings);
            _graphFactory = new GraphFactory(settings.BaseGroupRouter, loadGraph);

            // Load the graphs
            if (_graphFactory != null)
            {
                Console.WriteLine("Loading trust graph...");
                _trustGraph = _graphFactory.V2TrustGraph();

                Console.WriteLine("Loading balance graph...");
                _balanceGraph = _graphFactory.V2BalanceGraph();

                // Load groups dynamically from database
                Console.WriteLine("Loading groups...");
                var groups = loadGraph.LoadGroups();
                foreach (var groupAddress in groups)
                {
                    _dynamicGroups.Add(groupAddress.ToLowerInvariant());
                }

                Console.WriteLine($"Loaded {_dynamicGroups.Count} groups from database");

                // Load group trust relationships
                Console.WriteLine("Loading group trust relationships...");
                var groupTrusts = loadGraph.LoadGroupTrusts();
                foreach (var (groupAddress, trustedToken) in groupTrusts)
                {
                    var groupLower = groupAddress.ToLowerInvariant();
                    if (!_groupTrustedTokens.TryGetValue(groupLower, out var trustedSet))
                    {
                        trustedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        _groupTrustedTokens[groupLower] = trustedSet;
                    }

                    trustedSet.Add(trustedToken.ToLowerInvariant());
                }

                if (_trustGraph != null && _balanceGraph != null)
                {
                    Console.WriteLine(
                        $"{ConsoleColors.Green}Successfully loaded trust and balance graphs for validation{ConsoleColors.Reset}");
                    Console.WriteLine($"Trust graph: {_trustGraph.Edges.Count} trust relationships");
                    Console.WriteLine($"Balance graph: {_balanceGraph.BalanceNodes.Count} balances");
                    Console.WriteLine($"Groups: {_dynamicGroups.Count} registered groups");
                    _graphsLoaded = true;
                }
                else
                {
                    Console.WriteLine($"{ConsoleColors.Red}Failed to load one or more graphs{ConsoleColors.Reset}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"{ConsoleColors.Red}Failed to load graphs for validation: {ex.Message}{ConsoleColors.Reset}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            Console.WriteLine("Tests will run without trust and balance validation");
        }
    }

    /// <summary>
    /// Loads trust and balance graphs from the pathfinder's /snapshot endpoint
    /// </summary>
    private async Task LoadGraphsFromSnapshot()
    {
        try
        {
            // Get the current snapshot from the pathfinder
            var response = await _httpClient!.GetAsync($"{_pathfinderBaseUrl}/snapshot");

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                Console.WriteLine(
                    $"{ConsoleColors.Yellow}Pathfinder graphs not ready yet. Falling back to database.{ConsoleColors.Reset}");
                return;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            // Deserialize the snapshot
            var snapshot = JsonSerializer.Deserialize<NetworkSnapshotResponse>(json, _jsonOptions);

            if (snapshot == null)
            {
                Console.WriteLine($"{ConsoleColors.Red}Failed to deserialize snapshot response{ConsoleColors.Reset}");
                return;
            }

            Console.WriteLine($"Loaded snapshot at block {snapshot.BlockNumber}");
            Console.WriteLine($"Trust relationships: {snapshot.Trust.Count} trusters");
            Console.WriteLine($"Balance holders: {snapshot.Balance.Count}");

            // Build trust graph from snapshot
            _trustGraph = new TrustGraph();
            foreach (var (trusterId, trustees) in snapshot.Trust)
            {
                if (!_trustGraph.AvatarNodes.ContainsKey(trusterId))
                {
                    _trustGraph.AddAvatar(trusterId);
                }

                foreach (var trusteeId in trustees)
                {
                    if (!_trustGraph.AvatarNodes.ContainsKey(trusteeId))
                    {
                        _trustGraph.AddAvatar(trusteeId);
                    }

                    _trustGraph.AddTrustEdge(trusterId, trusteeId);
                }
            }

            // Build balance graph from snapshot
            _balanceGraph = new BalanceGraph();
            foreach (var (holderId, balanceNodes) in snapshot.Balance)
            {
                if (!_balanceGraph.AvatarNodes.ContainsKey(holderId))
                {
                    _balanceGraph.AddAvatar(holderId);
                }

                foreach (var node in balanceNodes)
                {
                    _balanceGraph.AddBalance(node.Holder, node.Token, node.Amount, node.IsWrapped, node.IsStatic);
                }
            }

            // Try to identify groups from snapshot using the router
            int routerId = AddressIdPool.IdOf(_routerAddress.ToLowerInvariant());

            // Check if router exists in trust data and what it trusts (likely groups)
            if (snapshot.Trust.TryGetValue(routerId, out var routerTrusts))
            {
                foreach (var trustedByRouter in routerTrusts)
                {
                    string address = AddressIdPool.StringOf(trustedByRouter);
                    _dynamicGroups.Add(address);
                }
            }

            // Extract group trust relationships
            foreach (var groupAddress in _dynamicGroups)
            {
                int groupId = AddressIdPool.IdOf(groupAddress);

                if (snapshot.Trust.TryGetValue(groupId, out var trustedTokens))
                {
                    var trustedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tokenId in trustedTokens)
                    {
                        trustedSet.Add(AddressIdPool.StringOf(tokenId));
                    }

                    _groupTrustedTokens[groupAddress] = trustedSet;
                }
            }

            // Try to load additional groups from database if available
            string? connectionString = Environment.GetEnvironmentVariable("POSTGRES_READONLY_CONNECTION_STRING");
            if (!string.IsNullOrEmpty(connectionString))
            {
                try
                {
                    var loadGraph = new LoadGraph(new Settings());
                    var groups = loadGraph.LoadGroups();
                    foreach (var groupAddress in groups)
                    {
                        _dynamicGroups.Add(groupAddress.ToLowerInvariant());
                    }

                    // Load group trust relationships
                    var groupTrusts = loadGraph.LoadGroupTrusts();
                    foreach (var (groupAddress, trustedToken) in groupTrusts)
                    {
                        var groupLower = groupAddress.ToLowerInvariant();
                        if (!_groupTrustedTokens.TryGetValue(groupLower, out var trustedSet))
                        {
                            trustedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _groupTrustedTokens[groupLower] = trustedSet;
                        }

                        trustedSet.Add(trustedToken.ToLowerInvariant());
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"{ConsoleColors.Yellow}Could not load additional groups from database: {ex.Message}{ConsoleColors.Reset}");
                }
            }

            _graphsLoaded = true;

            Console.WriteLine($"{ConsoleColors.Green}Successfully loaded graphs from snapshot{ConsoleColors.Reset}");
            Console.WriteLine($"Trust graph: {_trustGraph.Edges.Count} trust relationships");
            Console.WriteLine($"Balance graph: {_balanceGraph.BalanceNodes.Count} balance nodes");
            Console.WriteLine($"Groups: {_dynamicGroups.Count} groups identified");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleColors.Red}Failed to load snapshot: {ex.Message}{ConsoleColors.Reset}");
            _graphsLoaded = false;
        }
    }

    /// <summary>
    /// Check if an address is the Router
    /// </summary>
    private bool IsRouter(string address)
    {
        return address.Equals(_routerAddress, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if an address is a known Group
    /// </summary>
    private bool IsGroup(string address)
    {
        return _dynamicGroups.Contains(address.ToLowerInvariant());
    }

    /// <summary>
    /// Check if an address is a special node (Router or Group)
    /// </summary>
    private bool IsSpecialNode(string address)
    {
        return IsRouter(address) || IsGroup(address);
    }

    /// <summary>
    /// Check if a receiving address trusts the token they are receiving
    /// </summary>
    private bool CheckTrustRelationship(string receiver, string token)
    {
        if (!_graphsLoaded || _trustGraph == null)
        {
            Console.WriteLine("Trust graph not available for validation");
            return true; // Skip validation if graph not available
        }

        // Skip trust checks for Router (it's just a pass-through)
        if (IsRouter(receiver))
        {
            return true;
        }

        // For Groups receiving from Router, check if group trusts the token
        if (IsGroup(receiver))
        {
            var receiverLower = receiver.ToLowerInvariant();
            var tokenLower = token.ToLowerInvariant();

            if (_groupTrustedTokens.TryGetValue(receiverLower, out var trustedTokens))
            {
                return trustedTokens.Contains(tokenLower);
            }

            // If no trust info found, assume it's valid (was validated during edge creation)
            return true;
        }

        // Convert addresses to lowercase for comparison
        var receiverId = AddressIdPool.IdOf(receiver);
        var tokenId = AddressIdPool.IdOf(token);

        // Check if there's a trust edge from receiver to token
        foreach (var edge in _trustGraph.Edges)
        {
            if (edge.From == receiverId && edge.To == tokenId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Identify complete minting paths through Router and Groups
    /// </summary>
    private List<List<string>> IdentifyMintingPaths(List<TransferPathStep> transfers)
    {
        var paths = new List<List<string>>();

        // Look for transfers that go through Router
        var routerIncoming = transfers.Where(t => IsRouter(t.To)).ToList();

        foreach (var incoming in routerIncoming)
        {
            // Find the corresponding Router -> Group transfer
            var routerToGroup = transfers.FirstOrDefault(t =>
                IsRouter(t.From) &&
                IsGroup(t.To) &&
                t.TokenOwner.Equals(incoming.TokenOwner, StringComparison.OrdinalIgnoreCase));

            if (routerToGroup != null)
            {
                // Find the Group -> Avatar transfer (with group token)
                var groupToAvatar = transfers.FirstOrDefault(t =>
                    t.From.Equals(routerToGroup.To, StringComparison.OrdinalIgnoreCase) &&
                    t.TokenOwner.Equals(t.From, StringComparison.OrdinalIgnoreCase)); // Group token

                if (groupToAvatar != null)
                {
                    paths.Add([
                        incoming.From,
                        _routerAddress,
                        routerToGroup.To,
                        groupToAvatar.To
                    ]);
                }
            }
        }

        return paths;
    }

    /// <summary>
    /// Data class for JSON-RPC request
    /// </summary>
    private class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")] public int Id { get; set; } = 0;

        [JsonPropertyName("method")] public string Method { get; set; } = "";

        [JsonPropertyName("params")] public object[] Params { get; set; } = Array.Empty<object>();
    }

    /// <summary>
    /// Data class for JSON-RPC response
    /// </summary>
    private class JsonRpcResponse<T>
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")] public int Id { get; set; }

        [JsonPropertyName("result")] public T? Result { get; set; } = default;

        [JsonPropertyName("error")] public JsonRpcError? Error { get; set; }
    }

    /// <summary>
    /// Data class for JSON-RPC error
    /// </summary>
    private class JsonRpcError
    {
        [JsonPropertyName("code")] public int Code { get; set; }

        [JsonPropertyName("message")] public string Message { get; set; } = "";

        [JsonPropertyName("data")] public object? Data { get; set; }
    }

    /// <summary>
    /// Calculate the total flow based on a predicate
    /// </summary>
    private UInt256 CalculateTotalFlow(List<TransferPathStep> transfers, Func<TransferPathStep, bool> predicate)
    {
        UInt256 total = UInt256.Zero;
        foreach (var transfer in transfers.Where(predicate))
        {
            total += UInt256.Parse(transfer.Value);
        }

        return total;
    }

    /// <summary>
    /// Checks if there are any self-loops in the transfers (same from and to address)
    /// </summary>
    private bool HasSelfLoops(List<TransferPathStep> transfers)
    {
        foreach (var transfer in transfers)
        {
            if (transfer.From.Equals(transfer.To, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines if a token is wrapped by looking it up in the balance graph
    /// </summary>
    private bool IsWrappedToken(string accountAddress, string tokenAddress)
    {
        if (!_graphsLoaded || _balanceGraph == null)
        {
            Console.WriteLine("Balance graph not available for wrapped token validation");
            return false; // Skip validation if graph not available
        }

        var accountId = AddressIdPool.IdOf(accountAddress);
        var tokenId = AddressIdPool.IdOf(tokenAddress);

        // Find the balance node for this account and token
        string balanceNodeKey = $"{accountId}-{tokenId}";
        var balanceNodeId = AddressIdPool.BalanceNodeIdOf(balanceNodeKey);

        if (_balanceGraph.BalanceNodes.TryGetValue(balanceNodeId, out var balanceNode))
        {
            return balanceNode.IsWrapped;
        }

        // If no balance node found, check any balance node with this token
        foreach (var node in _balanceGraph.BalanceNodes.Values)
        {
            if (node.Token == tokenId)
            {
                return node.IsWrapped;
            }
        }

        // No information found
        return false;
    }

    /// <summary>
    /// Checks if source is sending tokens that are in the toTokens list when source=sink
    /// </summary>
    private bool HasInvalidTokensInSelfTransfer(List<TransferPathStep> transfers, string source, string sink,
        string[]? toTokens)
    {
        // Only perform this check when source equals sink and toTokens is specified
        if (!source.Equals(sink, StringComparison.OrdinalIgnoreCase) || toTokens == null || toTokens.Length == 0)
            return false;

        // Normalize for case-insensitive comparison
        source = source.ToLower();
        var toTokensLower = toTokens.Select(t => t.ToLower()).ToHashSet();

        // Check if source is sending any token that's in the toTokens list
        foreach (var transfer in transfers)
        {
            if (transfer.From.ToLower() == source)
            {
                // If the token being sent is in the toTokens list, this is invalid
                if (toTokensLower.Contains(transfer.TokenOwner.ToLower()))
                {
                    Console.WriteLine($"{ConsoleColors.Red}INVALID SELF-TRANSFER:{ConsoleColors.Reset} " +
                                      $"Source {transfer.From} is sending token {transfer.TokenOwner} " +
                                      $"which is in the toTokens list. This would create a circular flow.");
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Sends a JSON-RPC request to the pathfinder service and validates the response
    /// </summary>
    private async Task ValidatePathfinderResponse(
        string jsonRequest,
        string source,
        string sink,
        string[]? fromTokens = null,
        string[]? toTokens = null,
        string[]? excludedFromTokens = null,
        string[]? excludedToTokens = null,
        bool withWrap = false
    )
    {
        if (_httpClient == null)
        {
            throw new InvalidOperationException("HTTP client is not initialized");
        }

        var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

        bool validationPassed = true;

        try
        {
            var response = await _httpClient.PostAsync(_rpcUrl, content);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            var jsonResponse =
                JsonSerializer.Deserialize<JsonRpcResponse<MaxFlowResponse>>(responseContent, _jsonOptions);

            if (jsonResponse?.Error != null)
            {
                Console.WriteLine(
                    $"{ConsoleColors.Red}JSON-RPC Error: {jsonResponse.Error.Message}{ConsoleColors.Reset}");
                throw new Exception($"JSON-RPC error: {jsonResponse.Error.Message}");
            }

            var result = jsonResponse?.Result ?? throw new Exception("No result in response");

            // Print the results
            Console.WriteLine($"\n{ConsoleColors.Cyan}RESULTS:{ConsoleColors.Reset}");
            Console.WriteLine($"Max flow: {result.MaxFlow}");
            Console.WriteLine($"Transfers: {result.Transfers.Count}");

            // Check if there's no flow (zero max flow)
            if (result.MaxFlow == "0" || result.Transfers.Count == 0)
            {
                Console.WriteLine(
                    $"\n{ConsoleColors.Yellow}NO PATH FOUND:{ConsoleColors.Reset} No valid path exists between source and sink with the given constraints.");
                // FLOW ZERO CONSISTENCY CHECK
                Console.WriteLine($"\n{ConsoleColors.Magenta}FLOW ZERO CONSISTENCY CHECK:{ConsoleColors.Reset}");
                bool flowZeroConsistencyPassed = true;

                // Consistency check: if there are no transfers, maxFlow must be zero
                if (result.Transfers.Count == 0 && result.MaxFlow != "0")
                {
                    Console.WriteLine(
                        $"{ConsoleColors.Red}INCONSISTENT STATE:{ConsoleColors.Reset} No transfers found but MaxFlow is not zero ({result.MaxFlow})");
                    flowZeroConsistencyPassed = false;
                }

                // Consistency check: if maxFlow is zero, there should be no transfers
                if (result.MaxFlow == "0" && result.Transfers.Count > 0)
                {
                    Console.WriteLine(
                        $"{ConsoleColors.Red}INCONSISTENT STATE:{ConsoleColors.Reset} MaxFlow is zero but {result.Transfers.Count} transfers were found");
                    flowZeroConsistencyPassed = false;
                }

                Console.WriteLine(
                    $"Check: {(flowZeroConsistencyPassed ? ConsoleColors.Green + "PASSED" : ConsoleColors.Red + "FAILED")}{ConsoleColors.Reset}");
                if (!flowZeroConsistencyPassed)
                {
                    validationPassed = false;
                }

                // Finally, assert if the entire validation succeeded.
                if (validationPassed)
                {
                    Console.WriteLine($"\n{ConsoleColors.Green}Test completed successfully!{ConsoleColors.Reset}");
                }
                else
                {
                    Console.WriteLine(
                        $"\n{ConsoleColors.Red}Test failed due to one or more validation errors!{ConsoleColors.Reset}");
                }

                Assert.That(validationPassed, Is.True, "One or more validation checks failed.");
                return;
            }

            // Check for self-loops
            Console.WriteLine($"\n{ConsoleColors.Magenta}SELF-LOOP CHECK{ConsoleColors.Reset}");

            // Direct self-loops (same from and to address)
            bool hasDirectSelfLoops = HasSelfLoops(result.Transfers);

            // For source=sink cases, check if source is sending tokens in the toTokens list
            bool hasInvalidSelfTransfers = HasInvalidTokensInSelfTransfer(
                result.Transfers,
                source,
                sink,
                toTokens
            );

            bool selfLoopCheckPassed = !hasDirectSelfLoops && !hasInvalidSelfTransfers;

            Console.WriteLine(
                $"Direct self-loops: {(hasDirectSelfLoops ? ConsoleColors.Red + "DETECTED" : ConsoleColors.Green + "NONE")}{ConsoleColors.Reset}");
            Console.WriteLine(
                $"Invalid closed-paths: {(hasInvalidSelfTransfers ? ConsoleColors.Red + "DETECTED" : ConsoleColors.Green + "NONE")}{ConsoleColors.Reset}");
            Console.WriteLine(
                $"Check: {(selfLoopCheckPassed ? ConsoleColors.Green + "PASSED" : ConsoleColors.Red + "FAILED")}{ConsoleColors.Reset}");

            if (!selfLoopCheckPassed)
            {
                // Mark validation as failed
                validationPassed = false;
            }

            // Normalize addresses to lowercase for comparison
            source = source.ToLower();
            sink = sink.ToLower();

            // FLOW CONSERVATION CHECKS
            var transfers = result.Transfers;
            UInt256 totalOutflowFromSource = CalculateTotalFlow(transfers, t => t.From.ToLower() == source);
            UInt256 totalInflowToSink = CalculateTotalFlow(transfers, t => t.To.ToLower() == sink);
            UInt256 maxFlow = UInt256.Parse(result.MaxFlow);

            Console.WriteLine($"\n{ConsoleColors.Magenta}FLOW VALIDATION{ConsoleColors.Reset}");
            Console.WriteLine($"Total outflow from source: {totalOutflowFromSource}");
            Console.WriteLine($"Total inflow to sink: {totalInflowToSink}");
            Console.WriteLine($"Max flow: {maxFlow}");

            bool flowCheckPassed =
                totalOutflowFromSource == maxFlow &&
                totalInflowToSink == maxFlow;
            Console.WriteLine(
                $"Check: {(flowCheckPassed ? ConsoleColors.Green + "PASSED" : ConsoleColors.Red + "FAILED")}{ConsoleColors.Reset}");
            if (!flowCheckPassed)
            {
                validationPassed = false;
            }

            // Check intermediate nodes for flow conservation
            var intermediateNodes = transfers
                .Select(t => t.From.ToLower())
                .Concat(transfers.Select(t => t.To.ToLower()))
                .Distinct()
                .Where(addr => addr != source && addr != sink)
                .ToList();

            Console.WriteLine($"\n{ConsoleColors.Magenta}INTERMEDIATE NODE FLOW VALIDATION{ConsoleColors.Reset}");
            bool allIntermediateNodesValid = true;

            foreach (var node in intermediateNodes)
            {
                // Special handling for groups - they convert tokens
                if (IsGroup(node))
                {
                    var groupInflow = CalculateTotalFlow(transfers, t => t.To.ToLower() == node);
                    var groupOutflow = CalculateTotalFlow(transfers, t => t.From.ToLower() == node);

                    // For groups, inflow and outflow should match (minting preserves value)
                    bool groupFlowValid = groupInflow == groupOutflow;
                    if (!groupFlowValid)
                    {
                        Console.WriteLine(
                            $"{ConsoleColors.Red}Group {node} has unbalanced minting:{ConsoleColors.Reset} " +
                            $"inflow={groupInflow}, outflow={groupOutflow}");
                        allIntermediateNodesValid = false;
                    }
                    else
                    {
                        Console.WriteLine(
                            $"Group {node} minting: {groupInflow} -> {groupOutflow} {ConsoleColors.Green}VALID{ConsoleColors.Reset}");
                    }
                }
                else if (IsRouter(node))
                {
                    // Router should have balanced flow
                    var routerInflow = CalculateTotalFlow(transfers, t => t.To.ToLower() == node);
                    var routerOutflow = CalculateTotalFlow(transfers, t => t.From.ToLower() == node);

                    bool routerFlowValid = routerInflow == routerOutflow;
                    if (!routerFlowValid)
                    {
                        Console.WriteLine(
                            $"{ConsoleColors.Red}Router has unbalanced flow:{ConsoleColors.Reset} " +
                            $"inflow={routerInflow}, outflow={routerOutflow}");
                        allIntermediateNodesValid = false;
                    }
                }
                else
                {
                    // Normal intermediate node validation
                    var outflow = CalculateTotalFlow(transfers, t => t.From.ToLower() == node);
                    var inflow = CalculateTotalFlow(transfers, t => t.To.ToLower() == node);

                    bool nodeFlowValid = inflow == outflow;
                    if (!nodeFlowValid)
                    {
                        Console.WriteLine(
                            $"{ConsoleColors.Red}Intermediate node {node} has unbalanced flow:{ConsoleColors.Reset} inflow={inflow}, outflow={outflow}");
                        allIntermediateNodesValid = false;
                    }
                }
            }

            Console.WriteLine(
                $"Check: {(allIntermediateNodesValid ? ConsoleColors.Green + "PASSED" : ConsoleColors.Red + "FAILED")}{ConsoleColors.Reset}");
            if (!allIntermediateNodesValid)
            {
                validationPassed = false;
            }

            // TOKEN FILTER CHECKS
            if (fromTokens != null && fromTokens.Length > 0)
            {
                var sourceTransfers = transfers.Where(t => t.From.ToLower() == source).ToList();
                var tokensUsedFromSource = sourceTransfers.Select(t => t.TokenOwner.ToLower()).Distinct().ToList();

                Console.WriteLine($"\n{ConsoleColors.Magenta}FROM TOKENS FILTER CHECK{ConsoleColors.Reset}");

                // Check if all used tokens were in the FromTokens list
                var fromTokensLower = fromTokens.Select(t => t.ToLower()).ToHashSet();
                bool allValidFromTokens = tokensUsedFromSource.All(token => fromTokensLower.Contains(token));

                Console.WriteLine(
                    $"Check: {(allValidFromTokens ? ConsoleColors.Green + "PASSED" : ConsoleColors.Red + "FAILED")}{ConsoleColors.Reset}");
                if (!allValidFromTokens)
                {
                    validationPassed = false;
                }
            }

            if (toTokens != null && toTokens.Length > 0)
            {
                var sinkTransfers = transfers.Where(t => t.To.ToLower() == sink).ToList();
                var tokensUsedToSink = sinkTransfers.Select(t => t.TokenOwner.ToLower()).Distinct().ToList();

                Console.WriteLine($"\n{ConsoleColors.Magenta}TO TOKENS FILTER CHECK{ConsoleColors.Reset}");

                // Check if all used tokens were in the ToTokens list
                var toTokensLower = toTokens.Select(t => t.ToLower()).ToHashSet();
                bool allValidToTokens = tokensUsedToSink.All(token => toTokensLower.Contains(token));
                Console.WriteLine(
                    $"Check: {(allValidToTokens ? ConsoleColors.Green + "PASSED" : ConsoleColors.Red + "FAILED")}{ConsoleColors.Reset}");
                if (!allValidToTokens)
                {
                    validationPassed = false;
                }
            }

            // ROUTER AND GROUP VALIDATION
            Console.WriteLine($"\n{ConsoleColors.Magenta}ROUTER AND GROUP VALIDATION:{ConsoleColors.Reset}");
            bool routerGroupValidationPassed = true;

            // Check for router and group nodes in the path
            var routerTransfers = transfers.Where(t => IsRouter(t.From) || IsRouter(t.To)).ToList();
            var groupTransfers = transfers.Where(t => IsGroup(t.From) || IsGroup(t.To)).ToList();

            if (routerTransfers.Any() || groupTransfers.Any())
            {
                Console.WriteLine(
                    $"Found {routerTransfers.Count} router transfers and {groupTransfers.Count} group transfers");

                // Validate Router constraints
                foreach (var transfer in routerTransfers)
                {
                    if (IsRouter(transfer.From))
                    {
                        // Router can only send to groups
                        if (!IsGroup(transfer.To))
                        {
                            Console.WriteLine($"{ConsoleColors.Red}INVALID ROUTER TRANSFER:{ConsoleColors.Reset} " +
                                              $"Router sending to non-group address {transfer.To}");
                            routerGroupValidationPassed = false;
                        }
                    }

                    if (IsRouter(transfer.To))
                    {
                        // Router can only receive from avatars (not groups or itself)
                        if (IsSpecialNode(transfer.From))
                        {
                            Console.WriteLine($"{ConsoleColors.Red}INVALID ROUTER TRANSFER:{ConsoleColors.Reset} " +
                                              $"Router receiving from special node {transfer.From}");
                            routerGroupValidationPassed = false;
                        }
                    }
                }

                // Validate Group constraints
                foreach (var transfer in groupTransfers)
                {
                    if (IsGroup(transfer.From))
                    {
                        // Group sending - should be group token
                        if (!transfer.TokenOwner.Equals(transfer.From, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"{ConsoleColors.Red}INVALID GROUP MINTING:{ConsoleColors.Reset} " +
                                              $"Group {transfer.From} sending token {transfer.TokenOwner} instead of its own token");
                            routerGroupValidationPassed = false;
                        }

                        // Group should not send to Router or other groups
                        if (IsSpecialNode(transfer.To))
                        {
                            Console.WriteLine($"{ConsoleColors.Red}INVALID GROUP TRANSFER:{ConsoleColors.Reset} " +
                                              $"Group {transfer.From} sending to special node {transfer.To}");
                            routerGroupValidationPassed = false;
                        }
                    }

                    if (IsGroup(transfer.To))
                    {
                        // Group can only receive from Router
                        if (!IsRouter(transfer.From))
                        {
                            Console.WriteLine($"{ConsoleColors.Red}INVALID GROUP TRANSFER:{ConsoleColors.Reset} " +
                                              $"Group {transfer.To} receiving from non-router {transfer.From}");
                            routerGroupValidationPassed = false;
                        }
                    }
                }

                // Check for proper minting flow pattern: Avatar -> Router -> Group -> Avatar
                // This is a more complex check that looks for complete minting paths
                var mintingPaths = IdentifyMintingPaths(transfers);
                foreach (var path in mintingPaths)
                {
                    Console.WriteLine($"Minting path identified: {string.Join(" -> ", path)}");
                }
            }

            Console.WriteLine(
                $"Check: {(routerGroupValidationPassed ? ConsoleColors.Green + "PASSED" : ConsoleColors.Red + "FAILED")}{ConsoleColors.Reset}");
            if (!routerGroupValidationPassed)
            {
                validationPassed = false;
            }

            // TRUST RELATIONSHIP VALIDATION
            if (_graphsLoaded && _trustGraph != null)
            {
                Console.WriteLine($"\n{ConsoleColors.Magenta}TRUST RELATIONSHIP VALIDATION:{ConsoleColors.Reset}");
                bool allTrustRelationshipsValid = true;

                foreach (var transfer in transfers)
                {
                    // Skip self-transfers for same address
                    if (transfer.From.Equals(transfer.To, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Every receiving address should trust the token they're receiving
                    bool trustValid = CheckTrustRelationship(transfer.To, transfer.TokenOwner);

                    if (!trustValid)
                    {
                        allTrustRelationshipsValid = false;
                        Console.WriteLine(
                            $"{ConsoleColors.Red}INVALID TRUST:{ConsoleColors.Reset} {transfer.To} does not trust token {transfer.TokenOwner}");
                    }
                }

                Console.WriteLine(
                    $"Check: {(allTrustRelationshipsValid ? ConsoleColors.Green + "PASSED" : ConsoleColors.Red + "FAILED")}{ConsoleColors.Reset}");
                if (!allTrustRelationshipsValid)
                {
                    validationPassed = false;
                }
            }
            else
            {
                Console.WriteLine(
                    $"\n{ConsoleColors.Red}Trust validation skipped - graphs not loaded{ConsoleColors.Reset}");
            }

            // BALANCE VALIDATION
            if (_graphsLoaded && _balanceGraph != null)
            {
                Console.WriteLine($"\n{ConsoleColors.Magenta}BALANCE VALIDATION:{ConsoleColors.Reset}");
                bool allBalancesValid = true;

                // Calculate total outflow by token for each sender
                var senderTokenOutflows = new Dictionary<string, Dictionary<string, UInt256>>();

                foreach (var transfer in transfers)
                {
                    string sender = transfer.From.ToLower();
                    string token = transfer.TokenOwner.ToLower();
                    UInt256 value = UInt256.Parse(transfer.Value);

                    if (!senderTokenOutflows.TryGetValue(sender, out var outflows))
                    {
                        outflows = new Dictionary<string, UInt256>();
                        senderTokenOutflows[sender] = outflows;
                    }

                    if (outflows.TryGetValue(token, out var currentOutflow))
                    {
                        outflows[token] = currentOutflow + value;
                    }
                    else
                    {
                        outflows[token] = value;
                    }
                }

                // Check each sender has sufficient balance for all tokens they're sending
                foreach (var senderEntry in senderTokenOutflows)
                {
                    string sender = senderEntry.Key;

                    // Skip balance checks for Router and Groups (they don't need balances)
                    if (IsRouter(sender) || IsGroup(sender))
                    {
                        Console.WriteLine(
                            $"{ConsoleColors.Green}SPECIAL NODE:{ConsoleColors.Reset} {sender} is a special node (Router/Group), skipping balance check");
                        continue;
                    }

                    int senderId = AddressIdPool.IdOf(senderEntry.Key);

                    foreach (var tokenEntry in senderEntry.Value)
                    {
                        string token = tokenEntry.Key;
                        int tokenId = AddressIdPool.IdOf(token);
                        UInt256 requiredAmount = tokenEntry.Value;

                        // Find the balance in the graph
                        string balanceNodeKey = $"{senderId}-{tokenId}";
                        var balanceNodeId = AddressIdPool.BalanceNodeIdOf(balanceNodeKey);
                        UInt256 availableBalance = UInt256.Zero;

                        if (_balanceGraph.BalanceNodes.TryGetValue(balanceNodeId, out var balanceNode))
                        {
                            availableBalance = CirclesConverter.BlowUpToUInt256(balanceNode.Amount);
                        }

                        bool hasBalance = availableBalance >= requiredAmount;

                        if (!hasBalance)
                        {
                            allBalancesValid = false;
                            Console.WriteLine(
                                $"{ConsoleColors.Red}INSUFFICIENT BALANCE:{ConsoleColors.Reset} {sender} has {availableBalance} of token {token} but requires {requiredAmount}");
                        }
                    }
                }

                Console.WriteLine(
                    $"Check: {(allBalancesValid ? ConsoleColors.Green + "PASSED" : ConsoleColors.Red + "FAILED")}{ConsoleColors.Reset}");
                if (!allBalancesValid)
                {
                    validationPassed = false;
                }
            }
            else
            {
                Console.WriteLine(
                    $"\n{ConsoleColors.Red}Balance validation skipped - graphs not loaded{ConsoleColors.Reset}");
            }

            // WRAPPED TOKEN VALIDATION
            if (_graphsLoaded && _balanceGraph != null)
            {
                Console.WriteLine($"\n{ConsoleColors.Magenta}WRAPPED TOKEN VALIDATION:{ConsoleColors.Reset}");
                bool wrappedTokenCheckPassed = true;

                // For withWrap=false, no transfers should use wrapped tokens
                if (!withWrap)
                {
                    foreach (var transfer in transfers)
                    {
                        // Check if token is wrapped
                        bool isWrappedToken = IsWrappedToken(transfer.From, transfer.TokenOwner);

                        if (isWrappedToken)
                        {
                            wrappedTokenCheckPassed = false;
                            Console.WriteLine($"{ConsoleColors.Red}INVALID WRAPPED TOKEN USAGE:{ConsoleColors.Reset} " +
                                              $"Transfer from {transfer.From} to {transfer.To} uses wrapped token {transfer.TokenOwner} " +
                                              $"but withWrap=false");
                        }
                    }
                }
                // For withWrap=true, only the source can send wrapped tokens, and never to the sink
                else
                {
                    foreach (var transfer in transfers)
                    {
                        // Check if token is wrapped
                        bool isWrappedToken = IsWrappedToken(transfer.From, transfer.TokenOwner);

                        if (isWrappedToken)
                        {
                            // Only source can use wrapped tokens
                            if (!transfer.From.Equals(source, StringComparison.OrdinalIgnoreCase))
                            {
                                wrappedTokenCheckPassed = false;
                                Console.WriteLine(
                                    $"{ConsoleColors.Red}INVALID WRAPPED TOKEN USAGE:{ConsoleColors.Reset} " +
                                    $"Non-source account {transfer.From} is using wrapped token {transfer.TokenOwner}");
                            }
                        }
                    }
                }

                Console.WriteLine(
                    $"Check: {(wrappedTokenCheckPassed ? ConsoleColors.Green + "PASSED" : ConsoleColors.Red + "FAILED")}{ConsoleColors.Reset}");
                if (!wrappedTokenCheckPassed)
                {
                    validationPassed = false;
                }
            }
            else
            {
                Console.WriteLine(
                    $"\n{ConsoleColors.Red}Wrapped token validation skipped - graphs not loaded{ConsoleColors.Reset}");
            }


            // EXCLUDED FROM TOKENS FILTER CHECK
            if (excludedFromTokens != null && excludedFromTokens.Length > 0)
            {
                var sourceTransfers = transfers.Where(t => t.From.ToLower() == source.ToLower()).ToList();
                var tokensUsedFromSource = sourceTransfers.Select(t => t.TokenOwner.ToLower()).Distinct().ToList();

                Console.WriteLine($"\n{ConsoleColors.Magenta}EXCLUDED FROM TOKENS FILTER CHECK{ConsoleColors.Reset}");

                // Check if any excluded tokens were used from source
                var excludedFromTokensLower = excludedFromTokens.Select(t => t.ToLower()).ToHashSet();
                bool noExcludedFromTokensUsed =
                    tokensUsedFromSource.All(token => !excludedFromTokensLower.Contains(token));

                if (!noExcludedFromTokensUsed)
                {
                    var usedExcludedTokens = tokensUsedFromSource
                        .Where(token => excludedFromTokensLower.Contains(token))
                        .ToList();

                    foreach (var token in usedExcludedTokens)
                    {
                        Console.WriteLine($"{ConsoleColors.Red}INVALID TOKEN USAGE:{ConsoleColors.Reset} " +
                                          $"Source {source} is using token {token} which is in the excludedFromTokens list.");
                    }
                }

                Console.WriteLine(
                    $"Check: {(noExcludedFromTokensUsed ? ConsoleColors.Green + "PASSED" : ConsoleColors.Red + "FAILED")}{ConsoleColors.Reset}");
                if (!noExcludedFromTokensUsed)
                {
                    validationPassed = false;
                }
            }

            // EXCLUDED TO TOKENS FILTER CHECK
            if (excludedToTokens != null && excludedToTokens.Length > 0)
            {
                var sinkTransfers = transfers.Where(t => t.To.ToLower() == sink.ToLower()).ToList();
                var tokensUsedToSink = sinkTransfers.Select(t => t.TokenOwner.ToLower()).Distinct().ToList();

                Console.WriteLine($"\n{ConsoleColors.Magenta}EXCLUDED TO TOKENS FILTER CHECK{ConsoleColors.Reset}");

                // Check if any excluded tokens were used to sink
                var excludedToTokensLower = excludedToTokens.Select(t => t.ToLower()).ToHashSet();
                bool noExcludedToTokensUsed = tokensUsedToSink.All(token => !excludedToTokensLower.Contains(token));

                if (!noExcludedToTokensUsed)
                {
                    var usedExcludedTokens = tokensUsedToSink
                        .Where(token => excludedToTokensLower.Contains(token))
                        .ToList();

                    foreach (var token in usedExcludedTokens)
                    {
                        Console.WriteLine($"{ConsoleColors.Red}INVALID TOKEN USAGE:{ConsoleColors.Reset} " +
                                          $"Sink {sink} is receiving token {token} which is in the excludedToTokens list.");
                    }
                }

                Console.WriteLine(
                    $"Check: {(noExcludedToTokensUsed ? ConsoleColors.Green + "PASSED" : ConsoleColors.Red + "FAILED")}{ConsoleColors.Reset}");
                if (!noExcludedToTokensUsed)
                {
                    validationPassed = false;
                }
            }


            // Finally, assert if the entire validation succeeded.
            if (validationPassed)
            {
                Console.WriteLine($"\n{ConsoleColors.Green}Test completed successfully!{ConsoleColors.Reset}");
            }
            else
            {
                Console.WriteLine(
                    $"\n{ConsoleColors.Red}Test failed due to one or more validation errors!{ConsoleColors.Reset}");
            }

            Assert.That(validationPassed, Is.True, "One or more validation checks failed.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            Console.WriteLine(
                $"\n{ConsoleColors.Yellow}SERVICE UNAVAILABLE:{ConsoleColors.Reset} The pathfinder service is currently unavailable or overloaded.");
            Assert.Inconclusive("Pathfinder service is unavailable or overloaded");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"\n{ConsoleColors.Red}HTTP REQUEST ERROR:{ConsoleColors.Reset} {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Provide test cases for TestPathfinderRequest method
    /// </summary>
    public static IEnumerable<TestCaseData> GetPathfinderTestCases()
    {
        foreach (var testCase in GetTestCasesFromJson())
        {
            // Create a TestCaseData object
            var nunitTestCase = new TestCaseData(
                testCase.Source,
                testCase.Sink,
                testCase.TargetFlow,
                testCase.FromTokens.Length > 0 ? testCase.FromTokens : null,
                testCase.ToTokens.Length > 0 ? testCase.ToTokens : null,
                testCase.ExcludedFromTokens.Length > 0 ? testCase.ExcludedFromTokens : null,
                testCase.ExcludedToTokens.Length > 0 ? testCase.ExcludedToTokens : null,
                testCase.WithWrap
            );

            // Set the test name and description
            nunitTestCase = nunitTestCase.SetName(testCase.Name)
                .SetDescription(testCase.Description);

            yield return nunitTestCase;
        }
    }

    /// <summary>
    /// Gets test cases from the JSON file with fallback to hardcoded values
    /// </summary>
    private static IEnumerable<PathfinderTestCase> GetTestCasesFromJson()
    {
        string testCasesPath = Path.Combine(
            Path.GetDirectoryName(typeof(NetworkPathfinderTests).Assembly.Location) ?? "",
            "pathfinder-test-cases.json");

        if (!File.Exists(testCasesPath))
        {
            Console.WriteLine($"Warning: Test cases file not found at {testCasesPath}.");
            Console.WriteLine("Using hardcoded test cases instead.");

            // Return hardcoded test cases
            yield return new PathfinderTestCase
            {
                Name = "BasicPath",
                Description = "Basic path with no filters",
                Source = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                Sink = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                TargetFlow = "400000000000000000000"
            };

            yield return new PathfinderTestCase
            {
                Name = "PathWithFromTokensFilter",
                Description = "Path with FromTokens filter",
                Source = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                Sink = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                TargetFlow = "100000000000000000000",
                FromTokens = new[] { "0x59cf08d8f86dd8a19b71f2dcd8ed71f9c2a8a9da" }
            };

            yield return new PathfinderTestCase
            {
                Name = "PathWithToTokensFilter",
                Description = "Path with ToTokens filter",
                Source = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                Sink = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                TargetFlow = "100000000000000000000",
                ToTokens = new[] { "0x4a9affa9249f36fd0629f342c182a4e94a13c2e0" }
            };

            yield break;
        }

        string json = File.ReadAllText(testCasesPath);
        var testCases = JsonSerializer.Deserialize<List<PathfinderTestCase>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (testCases != null)
        {
            foreach (var testCase in testCases)
            {
                yield return testCase;
            }
        }
    }


    [Test, TestCaseSource(nameof(GetPathfinderTestCases))]
    public async Task TestPathfinderRequest(
        string source,
        string sink,
        string targetFlow,
        string[]? fromTokens,
        string[]? toTokens,
        string[]? excludedFromTokens,
        string[]? excludedToTokens,
        bool withWrap)
    {
        Console.WriteLine(
            $"\n{ConsoleColors.Magenta}RUNNING TEST CASE: {TestContext.CurrentContext.Test.Name}{ConsoleColors.Reset}");
        Console.WriteLine($"Description: {TestContext.CurrentContext.Test.Properties.Get("Description") ?? ""}");
        Console.WriteLine($"Source: {source}");
        Console.WriteLine($"Sink: {sink}");
        Console.WriteLine($"Target Flow: {targetFlow}");
        Console.WriteLine($"From Tokens: {(fromTokens == null ? "none" : string.Join(", ", fromTokens))}");
        Console.WriteLine($"To Tokens: {(toTokens == null ? "none" : string.Join(", ", toTokens))}");
        Console.WriteLine(
            $"Excluded From Tokens: {(excludedFromTokens == null ? "none" : string.Join(", ", excludedFromTokens))}");
        Console.WriteLine(
            $"Excluded To Tokens: {(excludedToTokens == null ? "none" : string.Join(", ", excludedToTokens))}");
        Console.WriteLine($"With Wrap: {withWrap}");

        // Create the flow request
        var request = new FlowRequest
        {
            Source = source,
            Sink = sink,
            TargetFlow = targetFlow,
            FromTokens = fromTokens?.ToList(),
            ToTokens = toTokens?.ToList(),
            ExcludedFromTokens = excludedFromTokens?.ToList(),
            ExcludedToTokens = excludedToTokens?.ToList(),
            WithWrap = withWrap
        };

        // Convert the request to a JSON-RPC request
        var jsonRpcRequest = new JsonRpcRequest
        {
            Method = "circlesV2_findPath",
            Params = new object[] { request }
        };

        string jsonRequest = JsonSerializer.Serialize(jsonRpcRequest, _jsonOptions);

        // Validate the response
        await ValidatePathfinderResponse(jsonRequest, source, sink, fromTokens, toTokens, excludedFromTokens,
            excludedToTokens, withWrap);
    }

    private class TestSettings : Circles.Pathfinder.Settings
    {
        public string IndexReadonlyDbConnectionString { get; }
        public string BaseGroupRouter { get; }

        public TestSettings()
        {
            // Safely try to get values, default to empty/dummy if not present to avoid crashing tests
            IndexReadonlyDbConnectionString = Environment.GetEnvironmentVariable("POSTGRES_READONLY_CONNECTION_STRING")
                                           ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
                                           ?? "";

            BaseGroupRouter = Environment.GetEnvironmentVariable("V2_BASE_GROUP_ROUTER")?.ToLowerInvariant()
                              ?? "0xdc287474114cc0551a81ddc2eb51783fbf34802f";
        }
    }
}