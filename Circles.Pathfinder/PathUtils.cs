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
        var adjacency = new Dictionary<int, List<SimpleEdge>>();

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
            queue.Enqueue(source);

            /* ---------- BFS restricted to positive-flow arcs ---------------- */
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                bool sinkReached = parent.ContainsKey(sink);
                if (sinkReached)
                {
                    break;
                }

                if (!adjacency.TryGetValue(current, out var outgoing))
                {
                    continue;
                }

                foreach (var edge in outgoing)
                {
                    bool edgeHasResidual = edge.Flow > 0;
                    bool nodeSeen = parent.ContainsKey(edge.To);
                    bool skipEdge = !edgeHasResidual || nodeSeen;
                    if (skipEdge)
                    {
                        continue;
                    }

                    parent[edge.To] = edge;
                    queue.Enqueue(edge.To);
                }
            }

            bool noPathFound = !parent.ContainsKey(sink);
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

            /* ----------------------------------------------------------------
             * Peel the bottleneck amount off the residual capacities so we can
             * search for the next augmenting path.
             * ---------------------------------------------------------------- */
            foreach (var edge in peel)
            {
                edge.Flow -= minFlow;
            }
        }

        return result;
    }
}