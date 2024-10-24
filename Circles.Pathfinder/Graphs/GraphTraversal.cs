using Circles.Pathfinder.Edges;

namespace Circles.Pathfinder.Graphs;

/// <summary>
/// Enumeration for specifying graph traversal algorithms.
/// </summary>
public enum TraversalType
{
    DFS,
    BFS
}

/// <summary>
/// Provides graph traversal methods using DFS and BFS.
/// </summary>
public static class GraphTraversal
{
    /// <summary>
    /// Traverses the graph from the start node to the end node using the specified traversal algorithm.
    /// Optionally filters edges based on a predicate and allows early termination after the first path.
    /// </summary>
    /// <typeparam name="TEdge">The type of edge in the graph, must inherit from Edge.</typeparam>
    /// <param name="graph">The graph instance.</param>
    /// <param name="start">The starting node identifier.</param>
    /// <param name="end">The ending node identifier.</param>
    /// <param name="traversalType">The traversal algorithm to use (DFS or BFS).</param>
    /// <param name="edgePredicate">A predicate to filter edges. If null, all edges are considered.</param>
    /// <param name="returnAfterFirst">If true, the traversal stops after finding the first path.</param>
    /// <returns>An enumerable of paths, where each path is a list of edges.</returns>
    public static IEnumerable<List<TEdge>> Traverse<TEdge>(
        this IGraph<TEdge> graph,
        string start,
        string end,
        TraversalType traversalType,
        Func<TEdge, bool>? edgePredicate = null,
        bool returnAfterFirst = false)
        where TEdge : Edge
    {
        edgePredicate ??= _ => true; // If no predicate is provided, consider all edges.

        switch (traversalType)
        {
            case TraversalType.DFS:
                return DfsTraverse(graph, start, end, edgePredicate, returnAfterFirst);
            case TraversalType.BFS:
                return BfsTraverse(graph, start, end, edgePredicate, returnAfterFirst);
            default:
                throw new ArgumentException("Invalid traversal type.");
        }
    }

    /// <summary>
    /// Performs an iterative DFS traversal.
    /// </summary>
    private static IEnumerable<List<TEdge>> DfsTraverse<TEdge>(
        IGraph<TEdge> graph,
        string start,
        string end,
        Func<TEdge, bool>? edgePredicate,
        bool returnAfterFirst)
        where TEdge : Edge
    {
        var stack = new Stack<(string currentNode, List<TEdge> path, HashSet<string> visited)>();
        stack.Push((start, new List<TEdge>(), new HashSet<string> { start }));

        while (stack.Count > 0)
        {
            var (current, path, visited) = stack.Pop();

            if (current == end)
            {
                yield return path;

                if (returnAfterFirst)
                    yield break;

                continue;
            }

            if (graph.Nodes.TryGetValue(current, out var node))
            {
                foreach (var edge in node.OutEdges.OfType<TEdge>().Where(edgePredicate).Reverse())
                {
                    if (!visited.Contains(edge.To))
                    {
                        var newPath = new List<TEdge>(path) { edge };
                        var newVisited = new HashSet<string>(visited) { edge.To };
                        stack.Push((edge.To, newPath, newVisited));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Performs an iterative BFS traversal.
    /// </summary>
    private static IEnumerable<List<TEdge>> BfsTraverse<TEdge>(
        IGraph<TEdge> graph,
        string start,
        string end,
        Func<TEdge, bool>? edgePredicate,
        bool returnAfterFirst)
        where TEdge : Edge
    {
        var queue = new Queue<(string currentNode, List<TEdge> path, HashSet<string> visited)>();
        queue.Enqueue((start, new List<TEdge>(), new HashSet<string> { start }));

        while (queue.Count > 0)
        {
            var (current, path, visited) = queue.Dequeue();

            if (current == end)
            {
                yield return path;

                if (returnAfterFirst)
                    yield break;

                continue;
            }

            if (graph.Nodes.TryGetValue(current, out var node))
            {
                foreach (var edge in node.OutEdges.OfType<TEdge>().Where(edgePredicate))
                {
                    if (!visited.Contains(edge.To))
                    {
                        var newPath = new List<TEdge>(path) { edge };
                        var newVisited = new HashSet<string>(visited) { edge.To };
                        queue.Enqueue((edge.To, newPath, newVisited));
                    }
                }
            }
        }
    }
}