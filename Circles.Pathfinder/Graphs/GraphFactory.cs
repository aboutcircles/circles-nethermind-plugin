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
        // We'll count occurrences of skip cases and log them at the end
        int skipBalanceCase1 = 0;
        int skipBalanceCase2 = 0;
        int skipBalanceCase3a = 0;
        int skipBalanceCase3b = 0;
        int skipCapEdgeCase1 = 0;
        int skipCapEdgeCase2 = 0;
        int skipTrustEdgeSinkTokens = 0;

        var stringWriter = new StringWriter();
        stringWriter.WriteLine("Creating capacity graph...");
        stringWriter.WriteLine($"Source: {request.Source}");
        stringWriter.WriteLine($"Sink: {request.Sink}");
        stringWriter.WriteLine($"ToTokens: {request.ToTokens?.Count ?? 0}");
        stringWriter.WriteLine($"FromTokens: {request.FromTokens?.Count ?? 0}");
        stringWriter.WriteLine($"WithWrap: {request.WithWrap}");
        Console.WriteLine(stringWriter.ToString());

        var capacityGraph = new CapacityGraph();

        // For convenience
        var sourceEqualsSink = (request.Source == request.Sink);
        var toTokensFilter = request.ToTokens?
                                 .Select(t => t.ToLower())
                                 .ToHashSet()
                             ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var fromTokensFilter = request.FromTokens?
                                   .Select(t => t.ToLower())
                                   .ToHashSet()
                               ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"Source equals sink: {sourceEqualsSink}");
        Console.WriteLine($"To tokens filter: {toTokensFilter.Count}");
        Console.WriteLine($"From tokens filter: {fromTokensFilter.Count}");

        // STEP 1: Create a unified list of nodes from both graphs
        foreach (var avatar in balanceGraph.AvatarNodes.Values)
        {
            capacityGraph.AddAvatar(avatar.Address);
        }

        foreach (var avatar in trustGraph.AvatarNodes.Values)
        {
            capacityGraph.AddAvatar(avatar.Address);
        }

        // Add BalanceNodes
        foreach (var balanceNode in balanceGraph.BalanceNodes.Values)
        {
            var isSource = balanceNode.HolderAddress == request.Source;

            // Case 1: skip certain balances if source == sink
            if (sourceEqualsSink && isSource && toTokensFilter.Count > 0 && toTokensFilter.Contains(balanceNode.Token))
            {
                skipBalanceCase1++;
                continue;
            }

            // Case 2: If fromTokens is specified, only include those tokens for the source address
            if (isSource && fromTokensFilter.Count > 0 && !fromTokensFilter.Contains(balanceNode.Token))
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
            if (balanceNode.IsWrapped && balanceNode.HolderAddress != request.Source)
            {
                skipBalanceCase3b++;
                continue;
            }

            capacityGraph.AddBalanceNode(balanceNode.Address, balanceNode.Token, balanceNode.Amount, balanceNode.IsWrapped, balanceNode.IsStatic);
        }

        // STEP 2: Copy capacity edges from the BalanceGraph
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

        // STEP 3: Create capacity edges based on the trust graph
        // Precompute trustee->trusters for quick lookups
        var trusteeToTrusters = new Dictionary<string, List<string>>();
        foreach (var edge in trustGraph.Edges)
        {
            // Example filter: If the sink is the one trusting,
            // and we have a toTokens filter, skip any trust to a token not in toTokens
            if (edge.From == request.Sink && toTokensFilter.Count > 0 && !toTokensFilter.Contains(edge.To))
            {
                skipTrustEdgeSinkTokens++;
                continue;
            }

            if (!trusteeToTrusters.TryGetValue(edge.To, out var trusters))
            {
                trusters = new List<string>();
                trusteeToTrusters[edge.To] = trusters;
            }

            trusters.Add(edge.From);
        }

        // For every balance node, see if there's a trust edge from token -> some node
        foreach (var balanceNode in balanceGraph.BalanceNodes.Values)
        {
            var tokenIssuer = balanceNode.Token.ToLower();
            if (trusteeToTrusters.TryGetValue(tokenIssuer, out var acceptingNodes))
            {
                foreach (var acceptingNode in acceptingNodes)
                {
                    // Avoid self edges
                    if (acceptingNode == balanceNode.HolderAddress)
                        continue;

                    if (!capacityGraph.Nodes.ContainsKey(balanceNode.Address)
                        || !capacityGraph.Nodes.ContainsKey(acceptingNode))
                    {
                        skipCapEdgeCase2++;
                        continue;
                    }

                    capacityGraph.AddCapacityEdge(
                        balanceNode.Address,
                        acceptingNode,
                        balanceNode.Token,
                        balanceNode.Amount
                    );
                }
            }
        }

        // STEP 4: Setup Virtual Sink if needed (i.e. if source == sink)
        //         so that we only add capacity edges for tokens actually trusted by the real sink.
        if (sourceEqualsSink && !string.IsNullOrEmpty(request.Source) && request.ToTokens?.Count > 0)
        {
            var lowerSource = request.Source.ToLower();
            var virtualSinkAddress = lowerSource + VIRTUAL_SINK_SUFFIX;

            // Add the virtual sink node
            capacityGraph.AddAvatar(virtualSinkAddress);

            // Track if any capacity edges were actually added to the virtual sink
            bool anyEdgesAdded = SetupVirtualSinkEdges(
                capacityGraph,
                trustGraph,
                balanceGraph,
                request.ToTokens,
                virtualSinkAddress
            );

            // Clean up if no edges were added
            if (!anyEdgesAdded)
            {
                capacityGraph.AvatarNodes.Remove(virtualSinkAddress);
                capacityGraph.Nodes.Remove(virtualSinkAddress);
            }
            else
            {
                // Store a reference on the capacity graph itself
                capacityGraph.VirtualSinkAddress = virtualSinkAddress;
            }
        }

        // After all the skipping logic, print out our aggregated counts
        Console.WriteLine($"Skipped balance nodes (case 1): {skipBalanceCase1}");
        Console.WriteLine($"Skipped balance nodes (case 2): {skipBalanceCase2}");
        Console.WriteLine($"Skipped balance nodes (case 3a): {skipBalanceCase3a}");
        Console.WriteLine($"Skipped balance nodes (case 3b): {skipBalanceCase3b}");
        Console.WriteLine($"Skipped capacity edges (node not found. Case: 1): {skipCapEdgeCase1}");
        Console.WriteLine($"Skipped capacity edges (node not found. Case: 2): {skipCapEdgeCase2}");
        Console.WriteLine($"Skipped trust edges (sink trusts token but not in toTokens): {skipTrustEdgeSinkTokens}");

        // Done
        return capacityGraph;
    }

    /// <summary>
    /// Sets up the virtual sink edges for tokens that the user trusts
    /// </summary>
    /// <returns>True if any edges were added, false otherwise</returns>
    private bool SetupVirtualSinkEdges(
        CapacityGraph capacityGraph,
        TrustGraph trustGraph,
        BalanceGraph balanceGraph,
        List<string> toTokens,
        string virtualSinkAddress)
    {
        bool anyEdgesAdded = false;

        // Process each destination token
        foreach (var tokenAddress in toTokens)
        {
            var token = tokenAddress.ToLower();

            // Find all accounts that trust this token
            var accountsTrustingToken = trustGraph.Edges
                .Where(e => e.To.Equals(token, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.From.ToLower())
                .ToList();

            foreach (var accountAddress in accountsTrustingToken)
            {
                // Skip if the account is the token itself
                if (accountAddress == token)
                    continue;

                // Skip if the account already has a balance of this token
                // This prevents self-loops
                if (HasBalanceEdge(balanceGraph, accountAddress, token))
                    continue;

                // Add capacity edge from the account to the virtual sink
                capacityGraph.AddCapacityEdge(
                    accountAddress,
                    virtualSinkAddress,
                    token,
                    long.MaxValue
                );

                anyEdgesAdded = true;
            }
        }

        return anyEdgesAdded;
    }

    /// <summary>
    /// Checks if an account has a balance of a specific token.
    /// </summary>
    private bool HasBalanceEdge(BalanceGraph balanceGraph, string from, string token)
    {
        return balanceGraph.Edges.Any(e =>
            e.From.Equals(from, StringComparison.OrdinalIgnoreCase) &&
            e.Token.Equals(token, StringComparison.OrdinalIgnoreCase));
    }

    public FlowGraph CreateFlowGraph(CapacityGraph capacityGraph)
    {
        var flowGraph = new FlowGraph();
        flowGraph.AddCapacity(capacityGraph);
        return flowGraph;
    }
}