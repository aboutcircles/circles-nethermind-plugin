using System.Numerics;
using Circles.Pathfinder.Edges;

namespace Circles.Pathfinder.Graphs;

public static class GraphExtensions
{
    /// <summary>
    /// Computes the maximum flow from source to sink in the FlowGraph and collects all augmenting paths.
    /// Stops when the target flow is met and prunes the flow to exactly match the target.
    /// </summary>
    /// <param name="graph">The flow graph instance.</param>
    /// <param name="source">The source node identifier.</param>
    /// <param name="sink">The sink node identifier.</param>
    /// <param name="targetFlow">The desired flow value to reach.</param>
    /// <returns>The total flow value up to the target flow.</returns>
    public static BigInteger ComputeMaxFlowWithPaths(
        this FlowGraph graph,
        string source,
        string sink,
        BigInteger targetFlow)
    {
        BigInteger maxFlow = 0;

        while (true)
        {
            // Use BFS to find an augmenting path
            var (path, pathFlow) = graph.Bfs(source, sink);

            if (pathFlow == 0)
            {
                break; // No more augmenting paths
            }

            // Calculate how much more flow we need
            BigInteger remainingFlow = targetFlow - maxFlow;

            // If the pathFlow exceeds the remaining targetFlow, prune it to the remaining amount
            if (pathFlow > remainingFlow)
            {
                pathFlow = remainingFlow;
            }

            // Update the flow and capacities along the path
            foreach (var edge in path)
            {
                edge.Flow += pathFlow;
                edge.CurrentCapacity -= pathFlow;

                if (edge.ReverseEdge != null)
                {
                    edge.ReverseEdge.CurrentCapacity += pathFlow;
                }
            }

            // Update the accumulated maxFlow
            maxFlow += pathFlow;

            // Stop if we've reached the target flow exactly
            if (maxFlow >= targetFlow)
            {
                break;
            }
        }

        // Return the accumulated flow, limited to the targetFlow
        return maxFlow;
    }

    /// <summary>
    /// Finds the shortest augmenting path in the FlowGraph using BFS and calculates the flow that can be pushed through it.
    /// </summary>
    /// <param name="graph">The flow graph instance.</param>
    /// <param name="source">The source node identifier.</param>
    /// <param name="sink">The sink node identifier.</param>
    /// <returns>A tuple containing the list of edges constituting the path and the flow that can be pushed through this path.</returns>
    private static (List<FlowEdge> path, BigInteger flow) Bfs(this FlowGraph graph, string source, string sink)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<(string node, List<FlowEdge> path)>();
        visited.Add(source);
        queue.Enqueue((source, new List<FlowEdge>()));

        while (queue.Count > 0)
        {
            var (currentNode, currentPath) = queue.Dequeue();

            // Check if the current node has outgoing edges
            if (!graph.Nodes.TryGetValue(currentNode, out var node))
            {
                continue;
            }

            // Iterate through all outgoing edges of the current node
            foreach (var edge in node.OutEdges.OfType<FlowEdge>())
            {
                if (edge.CurrentCapacity > 0 && !visited.Contains(edge.To))
                {
                    // Append the current edge to the path
                    var newPath = new List<FlowEdge>(currentPath) { edge };

                    // If the sink is reached, return the path immediately
                    if (edge.To == sink)
                    {
                        BigInteger pathFlow = newPath.Min(e => e.CurrentCapacity);
                        return (newPath, pathFlow);
                    }

                    // Otherwise, continue traversing
                    visited.Add(edge.To);
                    queue.Enqueue((edge.To, newPath));
                }
            }
        }

        // No valid path found
        return (new List<FlowEdge>(), 0);
    }
}