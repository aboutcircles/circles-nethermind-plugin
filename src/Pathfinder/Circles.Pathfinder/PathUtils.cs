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

        /* --------------------------------------------------------------------
         * Repeatedly peel one augmenting path at a time (classic Edmonds-Karp).
         * ------------------------------------------------------------------ */
        while (true)
        {
            var parent = new Dictionary<int, SimpleEdge>(); // child → edge used to reach it
            var queue = new Queue<int>();
            var seen = new HashSet<int> { source };
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
}