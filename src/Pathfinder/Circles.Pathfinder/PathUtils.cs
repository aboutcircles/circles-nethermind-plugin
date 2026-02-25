using Circles.Pathfinder.Edges;

namespace Circles.Pathfinder;

internal static class PathUtils
{
    public static List<List<SimpleEdge>> ExtractFlowPaths(
        IReadOnlyList<SimpleEdge> edges,
        int source,
        int sink)
    {
        /* --------------------------------------------------------------------
         * Build adjacency: map  node → List<SimpleEdge> that still have flow.
         * ------------------------------------------------------------------ */
        var adjacency = new Dictionary<int, List<SimpleEdge>>(capacity: Math.Max(4, edges.Count));

        foreach (var edge in edges)
        {
            bool edgeHasResidualFlow = edge.Flow > 0;
            if (!edgeHasResidualFlow)
            {
                continue;
            }

            if (!adjacency.TryGetValue(edge.From, out var list))
            {
                list = new List<SimpleEdge>();
                adjacency[edge.From] = list;
            }

            list.Add(edge);
        }

        var result = new List<List<SimpleEdge>>();

        // Reusable BFS buffers — cleared between iterations to avoid GC pressure
        var parent = new Dictionary<int, SimpleEdge>();
        var queue = new Queue<int>();
        var seen = new HashSet<int>();

        /* --------------------------------------------------------------------
         * Repeatedly peel one augmenting path at a time (classic Edmonds-Karp).
         * ------------------------------------------------------------------ */
        while (true)
        {
            parent.Clear();
            queue.Clear();
            seen.Clear();
            seen.Add(source);
            bool foundSink = false;

            queue.Enqueue(source);

            /* ---------- BFS restricted to positive-flow arcs ---------------- */
            while (queue.Count > 0 && !foundSink)
            {
                int current = queue.Dequeue();

                bool hasOutgoing = adjacency.TryGetValue(current, out var outgoing);
                if (!hasOutgoing)
                {
                    continue;
                }

                for (int i = 0; i < outgoing?.Count; i++)
                {
                    var edge = outgoing![i];

                    bool edgeHasResidual = edge.Flow > 0;
                    if (!edgeHasResidual)
                    {
                        continue;
                    }

                    bool newlySeen = seen.Add(edge.To);
                    if (!newlySeen)
                    {
                        continue;
                    }

                    parent[edge.To] = edge;

                    bool reachedSink = edge.To == sink;
                    if (reachedSink)
                    {
                        foundSink = true;
                        break;
                    }

                    queue.Enqueue(edge.To);
                }
            }

            bool noPathFound = !foundSink;
            if (noPathFound)
            {
                break;
            } // algorithm terminates

            /* ----------------------------------------------------------------
             * Walk back sink → source to collect edges in this path and
             * find the bottleneck capacity (minFlow).
             * ---------------------------------------------------------------- */
            var peel = new List<SimpleEdge>();
            long minFlow = long.MaxValue;
            int node = sink;

            while (node != source)
            {
                var edge = parent[node];
                peel.Add(edge);
                minFlow = Math.Min(minFlow, edge.Flow);
                node = edge.From;
            }

            peel.Reverse(); // now in source → sink order

            /* ----------------------------------------------------------------
             * Store an immutable copy of the path with the exact flow value.
             * ---------------------------------------------------------------- */
            var pathCopy = new List<SimpleEdge>(peel.Count);
            foreach (var edge in peel)
            {
                var copy = edge with { Flow = minFlow };
                pathCopy.Add(copy);
            }

            result.Add(pathCopy);

            // Peel the bottleneck amount off; prune depleted edges from adjacency.
            foreach (var edge in peel)
            {
                edge.Flow -= minFlow;

                bool depleted = edge.Flow <= 0;
                if (!depleted)
                {
                    continue;
                }

                if (adjacency.TryGetValue(edge.From, out var list))
                {
                    list.Remove(edge);
                    bool listEmpty = list.Count == 0;
                    if (listEmpty)
                    {
                        adjacency.Remove(edge.From);
                    }
                }
            }
        }

        return result;
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
