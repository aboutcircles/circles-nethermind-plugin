using Circles.Index.Utils;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.DTOs;
using Nethermind.Int256;

namespace Circles.Pathfinder.Graphs;

public class GraphFactory
{
    private const string VIRTUAL_SINK_SUFFIX = "_virtual_sink";

    /// <summary>
    /// Loads all v2 trust edges from the database and creates a trust graph from them.
    /// </summary>
    /// <returns>A trust graph containing all v2 trust edges.</returns>
    public TrustGraph V2TrustGraph(LoadGraph loadGraph)
    {
        var graph = new TrustGraph();
        var trustEdges = loadGraph.LoadV2Trust().ToArray();

        foreach (var trustEdge in trustEdges)
        {
            if (!graph.AvatarNodes.ContainsKey(trustEdge.Truster))
            {
                graph.AddAvatar(trustEdge.Truster);
            }

            if (!graph.AvatarNodes.ContainsKey(trustEdge.Trustee))
            {
                graph.AddAvatar(trustEdge.Trustee);
            }

            graph.AddTrustEdge(trustEdge.Truster, trustEdge.Trustee);
        }

        return graph;
    }

    /// <summary>
    /// Loads all v2 balances from the database and creates a balance graph from them.
    /// </summary>
    /// <returns>A balance graph containing all v2 balances and holders.</returns>
    public BalanceGraph V2BalanceGraph(LoadGraph loadGraph)
    {
        var graph = new BalanceGraph();
        var balances = loadGraph.LoadV2Balances().ToArray();

        foreach (var balance in balances)
        {
            if (!graph.AvatarNodes.ContainsKey(balance.Account))
            {
                graph.AddAvatar(balance.Account);
            }

            graph.AddBalance(
                balance.Account,
                balance.TokenAddress,
                ConversionUtils.TruncateToInt64(UInt256.Parse(balance.Balance)),
                balance.IsWrapped,
                balance.IsStatic);
        }

        return graph;
    }

    /// <summary>
    /// Takes a balance graph and a trust graph and creates a capacity graph from them.
    /// Also sets up a "virtual sink" if source == sink and toTokens are specified.
    /// </summary>
    /// <param name="balanceGraph">The balance graph to use.</param>
    /// <param name="trustGraph">The trust graph to use.</param>
    /// <param name="request">Flow request parameters.</param>
    /// <returns>A capacity graph created from the balance and trust graphs.</returns>
    public CapacityGraph CreateCapacityGraph(BalanceGraph balanceGraph, TrustGraph trustGraph, FlowRequest request)
    {
        // Initialize counters for skipped nodes/edges
        int skipBalanceCase2 = 0;    // fromTokens filter
        int skipBalanceCase3a = 0;   // withWrap=false filter
        int skipBalanceCase3b = 0;   // wrapped tokens not held by source
        int skipCapEdgeCase1 = 0;    // nodes not in capacity graph
        int skipCapEdgeCase2 = 0;    // trust-based edges missing nodes
        int skipTrustEdgeSinkTokens = 0; // sink->token trust filtered by toTokens

        LogRequestInfo(request);

        var capacityGraph = new CapacityGraph();

        // Setup key filters
        var sourceEqualsSink = (request.Source == request.Sink);
        var toTokensFilter = request.ToTokens?
                                .Select(t => t.ToLower())
                                .ToHashSet()
                            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var fromTokensFilter = request.FromTokens?
                                .Select(t => t.ToLower())
                                .ToHashSet()
                            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // STEP 1: Add all avatar nodes from both graphs
        AddAllAvatarNodes(capacityGraph, balanceGraph, trustGraph);

        // STEP 2: Create a virtual sink if needed
        string virtualSinkAddress = null;
        HashSet<string> virtualSinkTrustedTokens = new HashSet<string>();
        
        if (sourceEqualsSink && !string.IsNullOrEmpty(request.Source) && toTokensFilter.Count > 0)
        {
            (virtualSinkAddress, virtualSinkTrustedTokens) = 
                CreateVirtualSink(capacityGraph, request.Source, toTokensFilter, balanceGraph);
        }

        // STEP 3: Add balance nodes (applying filters)
        AddFilteredBalanceNodes(
            capacityGraph, 
            balanceGraph, 
            request, 
            sourceEqualsSink,
            fromTokensFilter,
            toTokensFilter,
            ref skipBalanceCase2, 
            ref skipBalanceCase3a, 
            ref skipBalanceCase3b);

        // STEP 4: Add capacity edges from balance graph
        AddCapacityEdges(capacityGraph, balanceGraph, ref skipCapEdgeCase1);

        // STEP 5: Remove direct BN->source edges that form self-loops for source==sink
        if (sourceEqualsSink && !string.IsNullOrEmpty(request.Source) && toTokensFilter.Count > 0)
        {
            RemoveSelfLoopEdges(capacityGraph, request.Source, toTokensFilter);
        }

        // STEP 6: Create account trust dictionary for efficient lookups
        var accountTrusts = BuildAccountTrustDictionary(
            trustGraph, request.Sink, toTokensFilter, ref skipTrustEdgeSinkTokens);

        // STEP 7: Add virtual sink edges if using virtual sink
        bool anyVirtualSinkEdgesAdded = false;
        if (virtualSinkAddress != null)
        {
            anyVirtualSinkEdgesAdded = AddVirtualSinkEdges(
                capacityGraph, balanceGraph, request.Source, virtualSinkAddress, virtualSinkTrustedTokens, accountTrusts);
            
            // Remove virtual sink if no edges were added
            if (!anyVirtualSinkEdgesAdded)
            {
                capacityGraph.AvatarNodes.Remove(virtualSinkAddress);
                capacityGraph.Nodes.Remove(virtualSinkAddress);
                capacityGraph.VirtualSinkAddress = null;
                Console.WriteLine("Removed virtual sink due to no edges");
            }
        }

        // STEP 8: Add regular trust-based capacity edges
        AddTrustBasedCapacityEdges(
            capacityGraph, balanceGraph, accountTrusts, virtualSinkAddress, ref skipCapEdgeCase2);

        // Log statistics about filtering
        LogFilteringStatistics(
            skipBalanceCase2, skipBalanceCase3a, skipBalanceCase3b,
            skipCapEdgeCase1, skipCapEdgeCase2, skipTrustEdgeSinkTokens);

        return capacityGraph;
    }

    /// <summary>
    /// Creates a flow graph from a capacity graph.
    /// </summary>
    public FlowGraph CreateFlowGraph(CapacityGraph capacityGraph)
    {
        var flowGraph = new FlowGraph();
        flowGraph.AddCapacity(capacityGraph);
        return flowGraph;
    }

    #region Helper Methods

    private void LogRequestInfo(FlowRequest request)
    {
        Console.WriteLine("Creating capacity graph...");
        Console.WriteLine($"Source: {request.Source}");
        Console.WriteLine($"Sink: {request.Sink}");
        Console.WriteLine($"ToTokens: {request.ToTokens?.Count ?? 0}");
        Console.WriteLine($"FromTokens: {request.FromTokens?.Count ?? 0}");
        Console.WriteLine($"WithWrap: {request.WithWrap}");
    }

    private void AddAllAvatarNodes(CapacityGraph capacityGraph, BalanceGraph balanceGraph, TrustGraph trustGraph)
    {
        foreach (var avatar in balanceGraph.AvatarNodes.Values)
        {
            capacityGraph.AddAvatar(avatar.Address);
        }

        foreach (var avatar in trustGraph.AvatarNodes.Values)
        {
            capacityGraph.AddAvatar(avatar.Address);
        }
    }

    private (string address, HashSet<string> trustedTokens) CreateVirtualSink(
        CapacityGraph capacityGraph, 
        string sourceAddress, 
        HashSet<string> toTokensFilter,
        BalanceGraph balanceGraph)
    {
        var lowerSource = sourceAddress.ToLower();
        var virtualSinkAddress = lowerSource + VIRTUAL_SINK_SUFFIX;

        capacityGraph.AddAvatar(virtualSinkAddress);
        capacityGraph.VirtualSinkAddress = virtualSinkAddress;

        Console.WriteLine($"Created virtual sink: {virtualSinkAddress}");

        // Collect tokens trusted by virtual sink (excluding wrapped tokens)
        var virtualSinkTrustedTokens = new HashSet<string>();
        foreach (var token in toTokensFilter)
        {
            bool isWrapped = balanceGraph.BalanceNodes.Values
                .Any(bn => bn.Token.Equals(token, StringComparison.OrdinalIgnoreCase) && bn.IsWrapped);

            if (isWrapped)
            {
                Console.WriteLine($"Skipping wrapped token {token} for virtual sink trust");
                continue;
            }

            virtualSinkTrustedTokens.Add(token);
            Console.WriteLine($"Virtual sink trusts token: {token}");
        }

        return (virtualSinkAddress, virtualSinkTrustedTokens);
    }

    private void AddFilteredBalanceNodes(
        CapacityGraph capacityGraph,
        BalanceGraph balanceGraph,
        FlowRequest request,
        bool sourceEqualsSink,
        HashSet<string> fromTokensFilter,
        HashSet<string> toTokensFilter,
        ref int skipBalanceCase2,
        ref int skipBalanceCase3a,
        ref int skipBalanceCase3b)
    {
        foreach (var balanceNode in balanceGraph.BalanceNodes.Values)
        {
            var isSource = balanceNode.HolderAddress.Equals(request.Source, StringComparison.OrdinalIgnoreCase);

            // Case 1: When source and sink are the same, don't add balances for tokens that are in toTokens
            // This prevents trivial self-loops when attempting token conversion
            if (sourceEqualsSink && isSource && toTokensFilter.Count > 0 && 
                toTokensFilter.Contains(balanceNode.Token.ToLower()))
            {
                Console.WriteLine($"Skipping balance of token {balanceNode.Token} for source=sink to prevent self-loop");
                continue;
            }

            // Case 2: If fromTokens is specified, only include those tokens for the source address
            if (isSource && fromTokensFilter.Count > 0 && !fromTokensFilter.Contains(balanceNode.Token.ToLower()))
            {
                skipBalanceCase2++;
                continue;
            }

            // Case 3a: Filter out wrapped tokens if request.WithWrap = false
            if (balanceNode.IsWrapped && (request.WithWrap == null || request.WithWrap == false))
            {
                skipBalanceCase3a++;
                continue;
            }

            // Case 3b: Filter out wrapped tokens not held by the source
            if (balanceNode.IsWrapped && !isSource)
            {
                skipBalanceCase3b++;
                continue;
            }

            capacityGraph.AddBalanceNode(
                balanceNode.Address,
                balanceNode.Token,
                balanceNode.Amount,
                balanceNode.IsWrapped,
                balanceNode.IsStatic
            );
        }
    }

    private void AddCapacityEdges(
        CapacityGraph capacityGraph, 
        BalanceGraph balanceGraph,
        ref int skipCapEdgeCase1)
    {
        foreach (var capacityEdge in balanceGraph.Edges)
        {
            if (!capacityGraph.Nodes.ContainsKey(capacityEdge.From)
                || !capacityGraph.Nodes.ContainsKey(capacityEdge.To))
            {
                skipCapEdgeCase1++;
                continue;
            }

            capacityGraph.AddCapacityEdge(
                capacityEdge.From,
                capacityEdge.To,
                capacityEdge.Token,
                capacityEdge.InitialCapacity
            );
        }
    }

    private void RemoveSelfLoopEdges(
        CapacityGraph capacityGraph, 
        string sourceAddress, 
        HashSet<string> toTokensFilter)
    {
        var lowerSource = sourceAddress.ToLower();

        capacityGraph.Edges.RemoveWhere(edge =>
            edge.From.StartsWith(lowerSource + "-", StringComparison.OrdinalIgnoreCase)
            && edge.To.Equals(lowerSource, StringComparison.OrdinalIgnoreCase)
            && toTokensFilter.Contains(edge.Token.ToLower())
        );
    }

    private Dictionary<string, HashSet<string>> BuildAccountTrustDictionary(
        TrustGraph trustGraph,
        string sinkAddress,
        HashSet<string> toTokensFilter,
        ref int skipTrustEdgeSinkTokens)
    {
        var accountTrusts = new Dictionary<string, HashSet<string>>();
        
        foreach (var edge in trustGraph.Edges)
        {
            // Skip trust edges from sink -> tokens not in toTokens
            if (edge.From.Equals(sinkAddress, StringComparison.OrdinalIgnoreCase)
                && toTokensFilter.Count > 0
                && !toTokensFilter.Contains(edge.To.ToLower()))
            {
                skipTrustEdgeSinkTokens++;
                continue;
            }

            var fromLower = edge.From.ToLower();
            var toLower = edge.To.ToLower();

            if (!accountTrusts.TryGetValue(fromLower, out var trustedTokens))
            {
                trustedTokens = new HashSet<string>();
                accountTrusts[fromLower] = trustedTokens;
            }
            trustedTokens.Add(toLower);
        }

        return accountTrusts;
    }

    private bool AddVirtualSinkEdges(
        CapacityGraph capacityGraph,
        BalanceGraph balanceGraph,
        string sourceAddress,
        string virtualSinkAddress,
        HashSet<string> virtualSinkTrustedTokens,
        Dictionary<string, HashSet<string>> accountTrusts)
    {
        bool anyVirtualSinkEdgesAdded = false;

        foreach (var balanceNode in balanceGraph.BalanceNodes.Values)
        {
            // Skip if the node doesn't exist in capacityGraph
            if (!capacityGraph.Nodes.ContainsKey(balanceNode.Address))
                continue;

            // Skip source's own balance nodes
            if (balanceNode.HolderAddress.Equals(sourceAddress, StringComparison.OrdinalIgnoreCase))
                continue;

            // Get holder address and token in lowercase
            var holderLower = balanceNode.HolderAddress.ToLower();
            var bnTokenLower = balanceNode.Token.ToLower();

            // Check if holder trusts this token
            if (accountTrusts.TryGetValue(holderLower, out var tokensTheyTrust))
            {
                // If holder trusts the token and virtual sink also trusts the token, add edge
                if (tokensTheyTrust.Contains(bnTokenLower) && virtualSinkTrustedTokens.Contains(bnTokenLower))
                {
                    if (capacityGraph.Nodes.ContainsKey(virtualSinkAddress))
                    {
                        capacityGraph.AddCapacityEdge(
                            balanceNode.Address,
                            virtualSinkAddress,
                            bnTokenLower,
                            balanceNode.Amount
                        );
                        anyVirtualSinkEdgesAdded = true;
                    }
                }
            }
        }

        return anyVirtualSinkEdgesAdded;
    }

    private void AddTrustBasedCapacityEdges(
        CapacityGraph capacityGraph,
        BalanceGraph balanceGraph,
        Dictionary<string, HashSet<string>> accountTrusts,
        string virtualSinkAddress,
        ref int skipCapEdgeCase2)
    {
        foreach (var balanceNode in balanceGraph.BalanceNodes.Values)
        {
            var tokenIssuer = balanceNode.Token.ToLower();

            // For each trusting account
            foreach (var (trustingAccount, trustedTokens) in accountTrusts)
            {
                // Skip if the account doesn't trust this BN's token
                if (!trustedTokens.Contains(tokenIssuer)) 
                    continue;

                // Skip self-edge
                if (trustingAccount == balanceNode.HolderAddress.ToLower())
                    continue;

                // Skip if it's the virtual sink (already handled above)
                if (trustingAccount == virtualSinkAddress?.ToLower())
                    continue;

                // Must exist in capacityGraph
                if (!capacityGraph.Nodes.ContainsKey(balanceNode.Address)
                    || !capacityGraph.Nodes.ContainsKey(trustingAccount))
                {
                    skipCapEdgeCase2++;
                    continue;
                }

                capacityGraph.AddCapacityEdge(
                    balanceNode.Address,
                    trustingAccount,
                    balanceNode.Token,
                    balanceNode.Amount
                );
            }
        }
    }

    private void LogFilteringStatistics(
        int skipBalanceCase2,
        int skipBalanceCase3a,
        int skipBalanceCase3b,
        int skipCapEdgeCase1,
        int skipCapEdgeCase2,
        int skipTrustEdgeSinkTokens)
    {
        Console.WriteLine($"Skipped balance nodes (case 1): [SOURCE=SINK toTokens skip]"); // Now used again
        Console.WriteLine($"Skipped balance nodes (case 2): {skipBalanceCase2}");
        Console.WriteLine($"Skipped balance nodes (case 3a): {skipBalanceCase3a}");
        Console.WriteLine($"Skipped balance nodes (case 3b): {skipBalanceCase3b}");
        Console.WriteLine($"Skipped capacity edges (node not found. Case: 1): {skipCapEdgeCase1}");
        Console.WriteLine($"Skipped capacity edges (node not found. Case: 2): {skipCapEdgeCase2}");
        Console.WriteLine($"Skipped trust edges (sink trusts token but not in toTokens): {skipTrustEdgeSinkTokens}");
    }

    #endregion
}