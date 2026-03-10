using Circles.Pathfinder.Edges;

namespace Circles.Pathfinder;

internal static class PathUtils
{
    /// <summary>
    /// Decomposes a solved flow into individual source-to-sink paths.
    /// Uses DFS with current-arc optimization: each edge is visited at most once
    /// across all path extractions, giving O(V + E) total complexity.
    /// </summary>
    public static List<List<SimpleEdge>> ExtractFlowPaths(
        IReadOnlyList<SimpleEdge> edges,
        int source,
        int sink)
    {
        // Source == sink: no valid path exists (would be a zero-length cycle)
        if (source == sink)
            return new List<List<SimpleEdge>>();

        // Build adjacency: node → list of edges with positive flow
        var adjacency = new Dictionary<int, List<SimpleEdge>>(capacity: Math.Max(4, edges.Count));

        foreach (var edge in edges)
        {
            if (edge.Flow <= 0)
                continue;

            if (!adjacency.TryGetValue(edge.From, out var list))
            {
                list = new List<SimpleEdge>();
                adjacency[edge.From] = list;
            }

            list.Add(edge);
        }

        var result = new List<List<SimpleEdge>>();

        // Current-arc pointers: track which edge index to try next per node.
        // This avoids re-scanning already-depleted edges on subsequent iterations.
        var currentArc = new Dictionary<int, int>();

        // DFS stack and cycle detection, reused between iterations
        var pathStack = new List<SimpleEdge>();
        var onStack = new HashSet<int>();

        while (true)
        {
            pathStack.Clear();
            onStack.Clear();
            onStack.Add(source);

            // DFS from source, following edges with remaining flow
            bool foundSink = DfsToSink(source, sink, adjacency, currentArc, pathStack, onStack);

            if (!foundSink)
                break;

            // Find bottleneck flow along the path
            long minFlow = long.MaxValue;
            foreach (var edge in pathStack)
            {
                if (edge.Flow < minFlow)
                    minFlow = edge.Flow;
            }

            // Store immutable copy with exact flow value
            var pathCopy = new List<SimpleEdge>(pathStack.Count);
            foreach (var edge in pathStack)
            {
                pathCopy.Add(edge with { Flow = minFlow });
            }
            result.Add(pathCopy);

            // Peel bottleneck flow; reset current-arc for nodes whose edge became depleted
            foreach (var edge in pathStack)
            {
                edge.Flow -= minFlow;

                if (edge.Flow <= 0 && adjacency.TryGetValue(edge.From, out var list))
                {
                    list.Remove(edge);
                    if (list.Count == 0)
                    {
                        adjacency.Remove(edge.From);
                        currentArc.Remove(edge.From);
                    }
                    else
                    {
                        // Reset arc pointer so we re-check from the start of the (now shorter) list
                        // This is safe because we only removed one element
                        var arcIdx = currentArc.GetValueOrDefault(edge.From, 0);
                        if (arcIdx >= list.Count)
                            currentArc[edge.From] = 0;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Iterative DFS from <paramref name="node"/> to <paramref name="sink"/> using current-arc pointers.
    /// Returns true if sink was reached; <paramref name="pathStack"/> contains the path (source→sink order).
    /// </summary>
    private static bool DfsToSink(
        int node,
        int sink,
        Dictionary<int, List<SimpleEdge>> adjacency,
        Dictionary<int, int> currentArc,
        List<SimpleEdge> pathStack,
        HashSet<int> onStack)
    {
        // Iterative DFS using the pathStack itself as the "call stack"
        while (true)
        {
            if (node == sink)
                return true;

            if (!adjacency.TryGetValue(node, out var outgoing))
            {
                // Dead end — backtrack
                if (pathStack.Count == 0)
                    return false;

                onStack.Remove(node);
                var lastEdge = pathStack[^1];
                pathStack.RemoveAt(pathStack.Count - 1);
                // Advance arc pointer past the failed edge
                currentArc[lastEdge.From] = currentArc.GetValueOrDefault(lastEdge.From, 0) + 1;
                node = lastEdge.From;
                continue;
            }

            int arcIdx = currentArc.GetValueOrDefault(node, 0);
            bool advanced = false;

            while (arcIdx < outgoing.Count)
            {
                var edge = outgoing[arcIdx];

                if (edge.Flow > 0 && onStack.Add(edge.To))
                {
                    // Found a usable edge — push and recurse
                    currentArc[node] = arcIdx;
                    pathStack.Add(edge);
                    node = edge.To;
                    advanced = true;
                    break;
                }

                arcIdx++;
            }

            if (!advanced)
            {
                // All edges from this node exhausted — backtrack
                currentArc[node] = arcIdx; // Mark as exhausted

                if (pathStack.Count == 0)
                    return false;

                onStack.Remove(node);
                var lastEdge = pathStack[^1];
                pathStack.RemoveAt(pathStack.Count - 1);
                currentArc[lastEdge.From] = currentArc.GetValueOrDefault(lastEdge.From, 0) + 1;
                node = lastEdge.From;
            }
        }
    }

    /// <summary>
    /// Quantizes sink-bound paths so each delivers an exact multiple of quantaSize.
    /// Paths with flow less than quantaSize are excluded.
    /// Only paths ending at the actual sink are quantized; intermediate paths pass through unchanged.
    /// </summary>
    /// <param name="paths">List of paths from ExtractFlowPaths</param>
    /// <param name="sink">The sink node ID</param>
    /// <param name="quantaSize">The quantization unit (e.g., 96 CRC in 6-decimal precision)</param>
    /// <param name="targetFlow">Target flow amount - determines max invites (targetFlow / quantaSize)</param>
    /// <returns>List of quantized paths, may have fewer paths than input</returns>
    public static List<List<SimpleEdge>> QuantizeSinkBoundFlows(
        List<List<SimpleEdge>> paths,
        int sink,
        long quantaSize,
        long targetFlow)
    {
        var result = new List<List<SimpleEdge>>();
        long totalQuantizedFlow = 0;

        foreach (var path in paths)
        {
            // Check if we've already reached the target
            if (totalQuantizedFlow >= targetFlow)
            {
                break;
            }

            // Check if this path ends at the sink
            if (path.Count == 0)
            {
                continue;
            }

            var lastEdge = path[^1];
            bool endsSink = lastEdge.To == sink;

            if (!endsSink)
            {
                // Non-sink-bound path - skip (only sink paths matter for invitations)
                continue;
            }

            // Get the path's current flow (all edges in a path have the same flow value)
            long pathFlow = path[0].Flow;

            // Calculate how many full quanta we can extract from this path
            long maxQuantaFromPath = pathFlow / quantaSize;

            if (maxQuantaFromPath <= 0)
            {
                // Path has less than 1 quanta - exclude it
                continue;
            }

            // Calculate how many more quanta we need to reach target
            long quantaNeeded = (targetFlow - totalQuantizedFlow) / quantaSize;
            long quantaToUse = Math.Min(maxQuantaFromPath, quantaNeeded);

            if (quantaToUse <= 0)
            {
                break;
            }

            // Create quantized path copy with exact multiple of quantaSize
            long quantizedFlow = quantaToUse * quantaSize;
            var quantizedPath = new List<SimpleEdge>(path.Count);

            foreach (var edge in path)
            {
                var quantizedEdge = edge with { Flow = quantizedFlow };
                quantizedPath.Add(quantizedEdge);
            }

            result.Add(quantizedPath);
            totalQuantizedFlow += quantizedFlow;
        }

        return result;
    }
}
