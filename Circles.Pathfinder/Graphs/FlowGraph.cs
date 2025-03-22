using System.Numerics;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;
using Nethermind.Int256;

namespace Circles.Pathfinder.Graphs;

public class FlowGraph : IGraph<FlowEdge>
{
    public IDictionary<string, Node> Nodes { get; } = new Dictionary<string, Node>();
    public IDictionary<string, AvatarNode> AvatarNodes { get; } = new Dictionary<string, AvatarNode>();
    public IDictionary<string, BalanceNode> BalanceNodes { get; } = new Dictionary<string, BalanceNode>();
    public HashSet<FlowEdge> Edges { get; } = new();

    public void AddAvatar(string avatarAddress)
    {
        if (!AvatarNodes.ContainsKey(avatarAddress))
        {
            AvatarNodes.Add(avatarAddress, new AvatarNode(avatarAddress));
            Nodes.Add(avatarAddress, AvatarNodes[avatarAddress]);
        }
    }

    public void AddBalanceNode(string address, string token, UInt256 amount)
    {
        var balanceNode = new BalanceNode(address, token, amount)
        {
            Address = address
        };
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

    public void AddFlowEdge(FlowGraph flowGraph, FlowEdge flowEdge)
    {
        var fromNode = flowGraph.Nodes[flowEdge.From];
        if (!Nodes.TryGetValue(fromNode.Address, out var from))
        {
            if (fromNode is AvatarNode)
            {
                AddAvatar(fromNode.Address);
            }
            else if (fromNode is BalanceNode fromBalance)
            {
                AddBalanceNode(fromBalance.Address, fromBalance.Token, fromBalance.Amount);
            }

            from = Nodes[fromNode.Address];
        }

        var toNode = flowGraph.Nodes[flowEdge.To];
        if (!Nodes.TryGetValue(toNode.Address, out var to))
        {
            if (toNode is AvatarNode)
            {
                AddAvatar(toNode.Address);
            }
            else if (toNode is BalanceNode toBalance)
            {
                AddBalanceNode(toBalance.Address, toBalance.Token, toBalance.Amount);
            }

            to = Nodes[toNode.Address];
        }

        // Avoid duplicates
        if (from.OutEdges.Any(o => o.To == to.Address && (o as FlowEdge)?.Flow == flowEdge.Flow))
        {
            return;
        }

        if (to.InEdges.Any(o => o.From == from.Address && (o as FlowEdge)?.Flow == flowEdge.Flow))
        {
            return;
        }

        var newFlowEdge = new FlowEdge(from.Address, to.Address, flowEdge.Token, flowEdge.CurrentCapacity)
        {
            Flow = flowEdge.Flow
        };

        var newReverseEdge = new FlowEdge(to.Address, from.Address, flowEdge.Token,
            flowEdge.ReverseEdge?.CurrentCapacity ?? UInt256.Zero)
        {
            Flow = flowEdge.ReverseEdge?.Flow ?? UInt256.Zero
        };

        newFlowEdge.ReverseEdge = newReverseEdge;
        newReverseEdge.ReverseEdge = newFlowEdge;

        Edges.Add(newFlowEdge);
        Edges.Add(newReverseEdge);

        if (AvatarNodes.TryGetValue(from.Address, out var fromAvatarNode))
        {
            fromAvatarNode.OutEdges.Add(newFlowEdge);
        }
        else if (BalanceNodes.TryGetValue(from.Address, out var fromBalanceNode))
        {
            fromBalanceNode.OutEdges.Add(newFlowEdge);
        }

        if (AvatarNodes.TryGetValue(to.Address, out var toAvatarNode))
        {
            toAvatarNode.InEdges.Add(newFlowEdge);
        }
        else if (BalanceNodes.TryGetValue(to.Address, out var toBalanceNode))
        {
            toBalanceNode.InEdges.Add(newFlowEdge);
        }
    }

    public void AddCapacityEdge(CapacityGraph capacityGraph, CapacityEdge capacityEdge)
    {
        var from = capacityEdge.From;
        var to = capacityEdge.To;
        var token = capacityEdge.Token;
        var capacity = capacityEdge.InitialCapacity;

        var edge = new FlowEdge(from, to, token, capacity);
        Edges.Add(edge);

        var reverseEdge = new FlowEdge(to, from, token, UInt256.Zero);
        Edges.Add(reverseEdge);

        edge.ReverseEdge = reverseEdge;
        reverseEdge.ReverseEdge = edge;

        // Ensure nodes exist
        var fromNode = capacityGraph.Nodes[from];
        if (fromNode is AvatarNode)
        {
            AddAvatar(fromNode.Address);
        }
        else if (fromNode is BalanceNode fromBalance)
        {
            AddBalanceNode(fromBalance.Address, fromBalance.Token, fromBalance.Amount);
        }

        var toNode = capacityGraph.Nodes[to];
        if (toNode is AvatarNode)
        {
            AddAvatar(toNode.Address);
        }
        else if (toNode is BalanceNode toBalance)
        {
            AddBalanceNode(toBalance.Address, toBalance.Token, toBalance.Amount);
        }

        // adjacency
        if (AvatarNodes.TryGetValue(from, out var nodeA))
        {
            nodeA.OutEdges.Add(edge);
        }
        else if (BalanceNodes.TryGetValue(from, out var balA))
        {
            balA.OutEdges.Add(edge);
        }

        if (AvatarNodes.TryGetValue(to, out var nodeB))
        {
            nodeB.InEdges.Add(edge);
        }
        else if (BalanceNodes.TryGetValue(to, out var balB))
        {
            balB.InEdges.Add(edge);
        }

        // adjacency for reverse
        if (AvatarNodes.TryGetValue(to, out var nodeC))
        {
            nodeC.OutEdges.Add(reverseEdge);
        }
        else if (BalanceNodes.TryGetValue(to, out var balC))
        {
            balC.OutEdges.Add(reverseEdge);
        }

        if (AvatarNodes.TryGetValue(from, out var nodeD))
        {
            nodeD.InEdges.Add(reverseEdge);
        }
        else if (BalanceNodes.TryGetValue(from, out var balD))
        {
            balD.InEdges.Add(reverseEdge);
        }
    }

    /// <summary>
    /// Performs a proper flow decomposition: repeatedly finds a path from source to sink
    /// in the flow network (only edges with e.Flow > 0), removes the min flow from each 
    /// edge on that path, and stores that as one path. Returns all disjoint paths.
    /// </summary>
    public List<List<FlowEdge>> ExtractPathsWithFlow(string sourceNode, string sinkNode, UInt256 minFlowThreshold)
    {
        var resultPaths = new List<List<FlowEdge>>();

        // Build adjacency lists containing only edges with positive flow
        var adjacency = new Dictionary<string, List<FlowEdge>>();
        foreach (var e in Edges)
        {
            if (e.Flow <= 0) continue;
            if (!adjacency.ContainsKey(e.From))
            {
                adjacency[e.From] = new List<FlowEdge>();
            }

            adjacency[e.From].Add(e);
        }

        while (true)
        {
            // BFS to find path from source -> sink
            var queue = new Queue<string>();
            var visited = new HashSet<string>();
            var parent = new Dictionary<string, FlowEdge>();

            queue.Enqueue(sourceNode);
            visited.Add(sourceNode);

            bool foundSink = false;
            while (queue.Count > 0 && !foundSink)
            {
                var current = queue.Dequeue();
                if (!adjacency.TryGetValue(current, out var value))
                {
                    continue;
                }

                foreach (var edge in value)
                {
                    if (visited.Contains(edge.To)) continue;
                    if (edge.Flow < minFlowThreshold) continue; // usually minFlowThreshold=0

                    visited.Add(edge.To);
                    parent[edge.To] = edge;

                    if (edge.To == sinkNode)
                    {
                        foundSink = true;
                        break;
                    }

                    queue.Enqueue(edge.To);
                }
            }

            if (!foundSink)
            {
                break; // no more augmenting paths
            }

            // Reconstruct path by backtracking from sinkNode -> sourceNode
            var pathEdges = new List<FlowEdge>();
            string node = sinkNode;
            while (node != sourceNode)
            {
                var e = parent[node];
                pathEdges.Add(e);
                node = e.From;
            }

            pathEdges.Reverse();

            // The path flow is the min edge.Flow along that path
            UInt256 pathFlow = pathEdges.Min(e => e.Flow);

            // Build a copy of the path with each edge having Flow=pathFlow
            var onePath = new List<FlowEdge>();
            foreach (var e in pathEdges)
            {
                var copy = new FlowEdge(e.From, e.To, e.Token, e.CurrentCapacity)
                {
                    Flow = pathFlow
                };
                onePath.Add(copy);
            }

            resultPaths.Add(onePath);

            // Subtract pathFlow from each edge in the path
            foreach (var e in pathEdges)
            {
                e.Flow -= pathFlow;

                // If e.Flow <= 0, remove it from adjacency so BFS won't use it next time
                if (e.Flow <= 0 && adjacency.ContainsKey(e.From))
                {
                    adjacency[e.From].Remove(e);
                }
            }
        }

        return resultPaths;
    }
}