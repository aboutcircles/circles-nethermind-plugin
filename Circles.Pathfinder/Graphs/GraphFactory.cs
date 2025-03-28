using Circles.Index.Utils;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.DTOs;
using Nethermind.Int256;

namespace Circles.Pathfinder.Graphs;

public class GraphFactory
{
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
                balance.IsWrapped);
        }

        return graph;
    }

    /// <summary>
    /// Takes a balance graph and a trust graph and creates a capacity graph from them.
    /// </summary>
    /// <param name="balanceGraph">The balance graph to use.</param>
    /// <param name="trustGraph">The trust graph to use.</param>
    /// <returns>A capacity graph created from the balance and trust graphs.</returns>
    public CapacityGraph CreateCapacityGraph(BalanceGraph balanceGraph, TrustGraph trustGraph, FlowRequest request)
    {
        var stringWriter = new StringWriter();
        stringWriter.WriteLine("Creating capacity graph...");
        stringWriter.WriteLine($"Source: {request.Source}");
        stringWriter.WriteLine($"Sink: {request.Sink}");
        stringWriter.WriteLine($"ToTokens: {request.ToTokens?.Count ?? 0}");
        stringWriter.WriteLine($"FromTokens: {request.FromTokens?.Count ?? 0}");
        stringWriter.WriteLine($"WithWrap: {request.WithWrap}");
        Console.WriteLine(stringWriter.ToString());

        // Take the balance and trust graphs and create a capacity graph.
        // 1. Create a unified list of nodes from both graphs
        // 2. Leave the capacity edges from the balance graph in place
        // 3. Create more capacity edges based on the trust graph:
        //    - For each balance, check if there is a node that is willing to accept the balance (is trusting the token issuer)
        //    - If there is, create a capacity edge from the balance node to the accepting node

        var capacityGraph = new CapacityGraph();

        var sourceEqualsSink = request.Source == request.Sink;
        Console.WriteLine($"Source equals sink: {sourceEqualsSink}");

        var toTokensFilter = request.ToTokens?.ToHashSet() ?? [];
        Console.WriteLine($"To tokens filter: {toTokensFilter.Count}");

        var fromTokensFilter = request.FromTokens?.ToHashSet() ?? [];
        Console.WriteLine($"From tokens filter: {fromTokensFilter.Count}");

        // Step 1: Create a unified list of nodes from both graphs
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
            // Case 1: When source and sink are the same, don't add balances for tokens that are in toTokens
            var isSource = balanceNode.HolderAddress == request.Source;
            if (sourceEqualsSink && isSource && toTokensFilter.Count > 0 && toTokensFilter.Contains(balanceNode.Token))
            {
                Console.WriteLine($"Skipping balance node (case 1) {balanceNode.Address}");
                continue;
            }

            // Case 2: When fromTokens is specified, only include specified tokens for source address
            if (isSource && fromTokensFilter.Count > 0 && !fromTokensFilter.Contains(balanceNode.Token))
            {
                Console.WriteLine($"Skipping balance node (case 2) {balanceNode.Address}");
                continue;
            }

            // Case 3: Filter wrapped tokens, only isWrapped tokens for source address are kept
            if (balanceNode.IsWrapped && (request.WithWrap == false ||
                                          (request.WithWrap == true && balanceNode.HolderAddress != request.Source)))
            {
                Console.WriteLine($"Skipping balance node (case 3) {balanceNode.Address}");
                continue;
            }

            capacityGraph.AddBalanceNode(balanceNode.Address, balanceNode.Token, balanceNode.Amount);
        }

        // Step 2: Leave the capacity edges from the balance graph in place
        foreach (var capacityEdge in balanceGraph.Edges)
        {
            capacityGraph.AddCapacityEdge(
                capacityEdge.From,
                capacityEdge.To,
                capacityEdge.Token,
                capacityEdge.InitialCapacity
            );
        }

        // Step 3: Create more capacity edges based on the trust graph
        // Optimization: Precompute a trustee-to-trusters lookup dictionary
        var trusteeToTrusters = new Dictionary<string, List<string>>();
        foreach (var edge in trustGraph.Edges)
        {
            // If this trust relation involves the sink trusting
            if (edge.From == request.Sink)
            {
                // If we have toTokens filter and the token (trustee) is not in toTokens, skip
                if (toTokensFilter.Count > 0 && !toTokensFilter.Contains(edge.To))
                {
                    Console.WriteLine(
                        $"Skipping trust edge (edge.From == request.Sink && not in filter) {edge.From} -> {edge.To}");
                    continue;
                }
            }

            if (!trusteeToTrusters.TryGetValue(edge.To, out var trusters))
            {
                trusters = new List<string>();
                trusteeToTrusters[edge.To] = trusters;
            }

            trusters.Add(edge.From);
        }

        foreach (var balanceNode in balanceGraph.BalanceNodes.Values)
        {
            string tokenIssuer = balanceNode.Token.ToLower();
            if (trusteeToTrusters.TryGetValue(tokenIssuer, out var acceptingNodes))
            {
                foreach (var acceptingNode in acceptingNodes)
                {
                    if (acceptingNode == balanceNode.HolderAddress)
                        continue;

                    capacityGraph.AddCapacityEdge(
                        balanceNode.Address,
                        acceptingNode,
                        balanceNode.Token,
                        balanceNode.Amount
                    );
                }
            }
        }

        return capacityGraph;
    }

    public FlowGraph CreateFlowGraph(CapacityGraph capacityGraph)
    {
        var flowGraph = new FlowGraph();
        flowGraph.AddCapacity(capacityGraph);
        return flowGraph;
    }
}