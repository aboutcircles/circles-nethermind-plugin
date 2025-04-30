using System.Diagnostics;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Graphs;

public readonly struct PathEdge
{
    public readonly FlowEdge Edge;
    public readonly long PathFlow;

    public PathEdge(FlowEdge edge, long pathFlow)
    {
        Edge = edge;
        PathFlow = pathFlow;
    }

    // Convenience forwarders – this lets the rest of the code stay unchanged
    public int From => Edge.From;
    public int To => Edge.To;
    public int Token => Edge.Token;
    public long Flow => PathFlow;
}

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

        var reverseEdge = new FlowEdge(to, from, token, 0);
        Edges.Add(reverseEdge);

        edge.ReverseEdge = reverseEdge;
        reverseEdge.ReverseEdge = edge;

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

        // manage adjacency lists
        if (AvatarNodes.TryGetValue(from, out var node))
        {
            node.OutEdges.Add(edge);
        }
        else if (BalanceNodes.TryGetValue(from, out var balanceNode))
        {
            balanceNode.OutEdges.Add(edge);
        }

        // if (AvatarNodes.TryGetValue(to, out var avatarNode))
        // {
        //     avatarNode.InEdges.Add(edge);
        // }
        // else if (BalanceNodes.TryGetValue(to, out var balanceNode))
        // {
        //     balanceNode.InEdges.Add(edge);
        // }

        if (AvatarNodes.TryGetValue(to, out var avatarNode2))
        {
            avatarNode2.OutEdges.Add(reverseEdge);
        }
        else if (BalanceNodes.TryGetValue(to, out var balanceNode2))
        {
            balanceNode2.OutEdges.Add(reverseEdge);
        }

        // if (AvatarNodes.TryGetValue(from, out var node2))
        // {
        //     node2.InEdges.Add(reverseEdge);
        // }
        // else if (BalanceNodes.TryGetValue(from, out var balanceNode2))
        // {
        //     balanceNode2.InEdges.Add(reverseEdge);
        // }
    }

    /// <summary>
    /// Searches the graph for liquid paths from the source node to the sink node.
    /// </summary>
    public List<List<PathEdge>> ExtractPathsWithFlow(
        int sourceNode,
        int sinkNode,
        long minFlowThreshold)
    {
        var sw = Stopwatch.StartNew();
        var resultPaths = new List<List<PathEdge>>();

        // Build an adjacency list that only contains edges with positive flow
        var adjacency = new Dictionary<int, List<FlowEdge>>();
        foreach (var e in Edges)
        {
            if (e.Flow <= 0) continue;
            if (!adjacency.TryGetValue(e.From, out var bucket))
            {
                bucket = new List<FlowEdge>(4);
                adjacency[e.From] = bucket;
            }

            bucket.Add(e);
        }

        var queue = new Queue<int>(Nodes.Count);
        var visited = new HashSet<int>(Nodes.Count);
        var parent = new Dictionary<int, FlowEdge>(Nodes.Count);

        while (true)
        {
            // BFS to find path from source -> sink
            queue.Clear();
            visited.Clear();
            parent.Clear();

            queue.Enqueue(sourceNode);
            visited.Add(sourceNode);

            var foundSink = false;

            while (queue.Count > 0 && !foundSink)
            {
                var current = queue.Dequeue();
                if (!adjacency.TryGetValue(current, out var outs))
                {
                    continue;
                }

                foreach (var edge in outs)
                {
                    if (edge.Flow < minFlowThreshold)
                    {
                        continue;
                    }

                    if (visited.Contains(edge.To))
                    {
                        continue;
                    }

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

            var tmpPath = new List<FlowEdge>(32);
            var node = sinkNode;

            while (node != sourceNode)
            {
                var e = parent[node];
                tmpPath.Add(e);
                node = e.From;
            }

            tmpPath.Reverse();

            // compute the bottleneck capacity of this path
            long pathFlow = long.MaxValue;
            foreach (var e in tmpPath)
            {
                if (e.Flow < pathFlow)
                {
                    pathFlow = e.Flow;
                }
            }

            // materialise the path without *copying* the edges
            var onePath = new List<PathEdge>(tmpPath.Count);
            foreach (var e in tmpPath)
            {
                onePath.Add(new PathEdge(e, pathFlow));
            }

            resultPaths.Add(onePath);

            // reduce residual capacities
            foreach (var e in tmpPath)
            {
                e.Flow -= pathFlow;
                if (e.Flow <= 0 && adjacency.TryGetValue(e.From, out var bucket))
                {
                    bucket.Remove(e);
                }
            }
        }

        sw.Stop();
        return resultPaths;
    }


    /// <summary>
    /// Aggregates identical edges in the flow graph, combining flows for edges with the same From, To, and Token.
    /// </summary>
    /// <returns>A new flow graph with aggregated edges.</returns>
    public FlowGraph AggregateIdenticalEdges()
    {
        Stopwatch sw = new Stopwatch();
        sw.Start();

        var aggregatedGraph = new FlowGraph(null, null);

        // Copy all avatar nodes
        foreach (var avatarNode in AvatarNodes.Values)
        {
            aggregatedGraph.AddAvatar(avatarNode.Address);
        }

        // Dictionary to store aggregated edges
        // Key is a tuple of (From, To, Token)
        var aggregatedEdges = new Dictionary<(int From, int To, int Token), FlowEdge>();

        // Aggregate edges with the same From, To, and Token
        foreach (var edge in Edges)
        {
            // Skip edges with zero flow
            if (edge.Flow <= 0)
            {
                continue;
            }

            var key = (edge.From, edge.To, edge.Token);

            if (aggregatedEdges.TryGetValue(key, out var existingEdge))
            {
                // Add the flow to the existing edge
                existingEdge.Flow += edge.Flow;

                // For capacity, take the max, as that's the logical limit
                existingEdge.CurrentCapacity = Math.Max(existingEdge.CurrentCapacity, edge.CurrentCapacity);
            }
            else
            {
                // Create a new aggregated edge
                var newEdge = new FlowEdge(edge.From, edge.To, edge.Token, edge.InitialCapacity)
                {
                    Flow = edge.Flow,
                    CurrentCapacity = edge.CurrentCapacity
                };

                aggregatedEdges[key] = newEdge;
            }
        }

        // Add all aggregated edges to the graph
        foreach (var edge in aggregatedEdges.Values)
        {
            try
            {
                // Instead of using AddFlowEdge, add directly to avoid validation checks
                // that might prevent adding the aggregated edge
                var fromNode = aggregatedGraph.Nodes[edge.From];
                var toNode = aggregatedGraph.Nodes[edge.To];

                var newFlowEdge = new FlowEdge(fromNode.Address, toNode.Address, edge.Token, edge.CurrentCapacity)
                {
                    Flow = edge.Flow
                };

                aggregatedGraph.Edges.Add(newFlowEdge);

                if (aggregatedGraph.AvatarNodes.TryGetValue(fromNode.Address, out var fromAvatarNode))
                {
                    fromAvatarNode.OutEdges.Add(newFlowEdge);
                }
                else if (aggregatedGraph.BalanceNodes.TryGetValue(fromNode.Address, out var fromBalanceNode))
                {
                    fromBalanceNode.OutEdges.Add(newFlowEdge);
                }

                // if (aggregatedGraph.AvatarNodes.TryGetValue(toNode.Address, out var toAvatarNode))
                // {
                //     toAvatarNode.InEdges.Add(newFlowEdge);
                // }
                // else if (aggregatedGraph.BalanceNodes.TryGetValue(toNode.Address, out var toBalanceNode))
                // {
                //     toBalanceNode.InEdges.Add(newFlowEdge);
                // }
            }
            catch (Exception ex)
            {
                // Console.WriteLine($"Error adding aggregated edge: {ex.Message}");
            }
        }

        sw.Stop();
        // Console.WriteLine($"TIMING: FlowGraph.AggregateIdenticalEdges took {sw.ElapsedMilliseconds}ms");

        return aggregatedGraph;
    }
}