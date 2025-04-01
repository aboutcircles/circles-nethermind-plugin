using System.Diagnostics;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

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

    public void AddBalanceNode(string address, string token, long amount)
    {
        var balanceNode = new BalanceNode(address, token, amount);
        balanceNode.Address = address;
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

        // TODO: Find a quicker way to do this
        if (from.OutEdges.Any(o => o.To == to.Address && (o as FlowEdge)?.Flow == flowEdge.Flow))
        {
            return;
        }

        if (to.InEdges.Any(o => o.From == from.Address && (o as FlowEdge)?.Flow == flowEdge.Flow))
        {
            return;
        }

        var newFlowEdge = new FlowEdge(from.Address, to.Address, flowEdge.Token, flowEdge.CurrentCapacity);
        newFlowEdge.Flow = flowEdge.Flow;

        var newReverseEdge = new FlowEdge(to.Address, from.Address, flowEdge.Token,
            flowEdge.ReverseEdge?.CurrentCapacity ?? 0L);
        newReverseEdge.Flow = flowEdge.ReverseEdge?.Flow ?? 0L;

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
                AddBalanceNode(fromBalance.Address, fromBalance.Token, fromBalance.Amount);
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
                AddBalanceNode(toBalance.Address, toBalance.Token, toBalance.Amount);
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

        if (AvatarNodes.TryGetValue(to, out var avatarNode))
        {
            avatarNode.InEdges.Add(edge);
        }
        else if (BalanceNodes.TryGetValue(to, out var balanceNode))
        {
            balanceNode.InEdges.Add(edge);
        }

        if (AvatarNodes.TryGetValue(to, out var avatarNode2))
        {
            avatarNode2.OutEdges.Add(reverseEdge);
        }
        else if (BalanceNodes.TryGetValue(to, out var balanceNode2))
        {
            balanceNode2.OutEdges.Add(reverseEdge);
        }

        if (AvatarNodes.TryGetValue(from, out var node2))
        {
            node2.InEdges.Add(reverseEdge);
        }
        else if (BalanceNodes.TryGetValue(from, out var balanceNode2))
        {
            balanceNode2.InEdges.Add(reverseEdge);
        }
    }

    /// <summary>
    /// Performs a proper flow decomposition: repeatedly finds a path from source to sink
    /// in the flow network (only edges with e.Flow > 0), removes the min flow from each 
    /// edge on that path, and stores that as one path. Returns all disjoint paths.
    /// </summary>
    public List<List<FlowEdge>> ExtractPathsWithFlow(string sourceNode, string sinkNode, long minFlowThreshold)
    {
        Stopwatch sw = new Stopwatch();
        sw.Start();
        
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
            long pathFlow = pathEdges.Min(e => e.Flow);

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
        
        sw.Stop();
        Console.WriteLine($"TIMING: FlowGraph.ExtractPathsWithFlow took {sw.ElapsedMilliseconds}ms");

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
        
        var aggregatedGraph = new FlowGraph();
        
        // Copy all avatar nodes
        foreach (var avatarNode in AvatarNodes.Values)
        {
            aggregatedGraph.AddAvatar(avatarNode.Address);
        }
        
        // Dictionary to store aggregated edges
        // Key is a tuple of (From, To, Token)
        var aggregatedEdges = new Dictionary<(string From, string To, string Token), FlowEdge>();
        
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
                
                if (aggregatedGraph.AvatarNodes.TryGetValue(toNode.Address, out var toAvatarNode))
                {
                    toAvatarNode.InEdges.Add(newFlowEdge);
                }
                else if (aggregatedGraph.BalanceNodes.TryGetValue(toNode.Address, out var toBalanceNode))
                {
                    toBalanceNode.InEdges.Add(newFlowEdge);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding aggregated edge: {ex.Message}");
            }
        }
        
        sw.Stop();
        Console.WriteLine($"TIMING: FlowGraph.AggregateIdenticalEdges took {sw.ElapsedMilliseconds}ms");
        
        return aggregatedGraph;
    }
}