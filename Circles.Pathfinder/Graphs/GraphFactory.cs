using System.Numerics;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.DTOs;

namespace Circles.Pathfinder.Graphs;

public class GraphFactory
{
    /// <summary>
    /// Loads all v2 trust edges from the database and creates a trust graph from them.
    /// </summary>
    /// <returns>A trust graph containing all v2 trust edges.</returns>
    public TrustGraph V2TrustGraph(LoadGraph loadGraph, FlowRequest? request = null)
    {
        var graph = new TrustGraph(request?.ToTokens, request?.Sink);
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
    public BalanceGraph V2BalanceGraph(LoadGraph loadGraph, FlowRequest? request = null)
    {
        var graph = new BalanceGraph(request?.FromTokens, request?.Source, request?.ToTokens, request?.Sink);
        var balances = loadGraph.LoadV2Balances().ToArray();
        
        foreach (var balance in balances)
        {
            if (!graph.AvatarNodes.ContainsKey(balance.Account))
            {
                graph.AddAvatar(balance.Account);
            }

            graph.AddBalance(balance.Account, balance.TokenAddress, BigInteger.Parse(balance.Balance));
        }

        return graph;
    }

    /// <summary>
    /// Takes a balance graph and a trust graph and creates a capacity graph from them.
    /// </summary>
    public CapacityGraph CreateCapacityGraph(BalanceGraph balanceGraph, TrustGraph trustGraph)
    {
        var capacityGraph = new CapacityGraph();

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
        var trusteeToTrusters = new Dictionary<string, List<string>>();
        foreach (var edge in trustGraph.Edges)
        {
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