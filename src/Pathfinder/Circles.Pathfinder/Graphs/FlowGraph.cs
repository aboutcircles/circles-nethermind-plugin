using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Graphs;

public class FlowGraph : IGraph<FlowEdge>
{
    public IDictionary<int, Node> Nodes { get; } = new Dictionary<int, Node>();
    public IDictionary<int, AvatarNode> AvatarNodes { get; } = new Dictionary<int, AvatarNode>();
    public IDictionary<int, BalanceNode> BalanceNodes { get; } = new Dictionary<int, BalanceNode>();
    public List<FlowEdge> Edges { get; } = new();

    public FlowGraph(int? nodeCount = null, int? edgeCount = null)
    {
        if (nodeCount != null)
        {
            Nodes = new Dictionary<int, Node>(nodeCount.Value);
        }

        if (edgeCount != null)
        {
            Edges = new List<FlowEdge>(edgeCount.Value);
        }
    }

    public void AddAvatar(int avatarAddress)
    {
        if (!AvatarNodes.ContainsKey(avatarAddress))
        {
            AvatarNodes.Add(avatarAddress, new AvatarNode(avatarAddress));
            Nodes.Add(avatarAddress, AvatarNodes[avatarAddress]);
        }
    }

    public void AddBalanceNode(int address, int token, long amount, bool isWrapped, bool isStatic)
    {
        var balanceNodeId = AddressIdPool.BalanceNodeIdOf($"{address}-{token}");
        var balanceNode = new BalanceNode(balanceNodeId, address, token, amount, isWrapped, isStatic);
        BalanceNodes.TryAdd(balanceNode.Address, balanceNode);
        Nodes.TryAdd(balanceNode.Address, balanceNode);
    }

    public void AddCapacity(CapacityGraph capacityGraph)
    {
        foreach (var capacityEdge in capacityGraph.Edges)
        {
            AddCapacityEdge(capacityGraph, capacityEdge);
        }
    }

    public void AddCapacityEdge(CapacityGraph capacityGraph, CapacityEdge capacityEdge)
    {
        var from = capacityEdge.From;
        var to = capacityEdge.To;
        var token = capacityEdge.Token;
        var capacity = capacityEdge.InitialCapacity;

        // Create edge and reverse edge
        var edge = new FlowEdge(from, to, token, capacity);
        Edges.Add(edge);

        // Create nodes if they don't exist
        if (capacityGraph.Nodes.TryGetValue(from, out var fromNode))
        {
            if (fromNode is AvatarNode)
            {
                AddAvatar(fromNode.Address);
            }
            else if (fromNode is BalanceNode fromBalance)
            {
                AddBalanceNode(fromBalance.Address, fromBalance.Token, fromBalance.Amount, fromBalance.IsWrapped,
                    fromBalance.IsStatic);
            }
        }

        if (capacityGraph.Nodes.TryGetValue(to, out var toNode))
        {
            if (toNode is AvatarNode)
            {
                AddAvatar(toNode.Address);
            }
            else if (toNode is BalanceNode toBalance)
            {
                AddBalanceNode(toBalance.Address, toBalance.Token, toBalance.Amount, toBalance.IsWrapped,
                    toBalance.IsStatic);
            }
        }
    }


    /// <summary>
    /// Aggregates identical edges in the flow graph, combining flows for edges with the same From, To, and Token.
    /// Operates in-place: replaces the Edges list with aggregated results, preserving all nodes.
    /// </summary>
    /// <returns>This graph instance (for chaining).</returns>
    public FlowGraph AggregateIdenticalEdges()
    {
        var aggregatedEdges = new Dictionary<(int From, int To, int Token), FlowEdge>(Edges.Count);

        foreach (var edge in Edges)
        {
            if (edge.Flow <= 0) continue;

            var key = (edge.From, edge.To, edge.Token);

            if (aggregatedEdges.TryGetValue(key, out var existingEdge))
            {
                // Saturating addition to prevent overflow
                existingEdge.Flow = existingEdge.Flow > long.MaxValue - edge.Flow
                    ? long.MaxValue
                    : existingEdge.Flow + edge.Flow;

                existingEdge.CurrentCapacity = Math.Max(existingEdge.CurrentCapacity, edge.CurrentCapacity);
            }
            else
            {
                aggregatedEdges[key] = new FlowEdge(edge.From, edge.To, edge.Token, edge.InitialCapacity)
                {
                    Flow = edge.Flow,
                    CurrentCapacity = edge.CurrentCapacity
                };
            }
        }

        // Replace edges list in-place
        Edges.Clear();
        Edges.AddRange(aggregatedEdges.Values);

        return this;
    }
}
