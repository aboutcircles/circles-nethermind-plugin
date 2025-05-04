using Circles.Pathfinder.Edges;

namespace Circles.Pathfinder;

internal static class PathUtils
{
    public static List<List<SimpleEdge>> ExtractFlowPaths(
        IReadOnlyList<SimpleEdge> edges,
        int source,
        int sink)
    {
        /* build adjacency (only positive-flow arcs) */
        var adjacency = edges
            .GroupBy(e => e.From)
            .ToDictionary(g => g.Key, g => g.ToList());

        var paths = new List<List<SimpleEdge>>();

        /* classic BFS path peeling */
        while (true)
        {
            var queue   = new Queue<int>();
            var parent  = new Dictionary<int, SimpleEdge>();
            queue.Enqueue(source);

            while (queue.Count > 0 && !parent.ContainsKey(sink))
            {
                int current = queue.Dequeue();
                if (!adjacency.TryGetValue(current, out var outs)) { continue; }

                foreach (SimpleEdge e in outs)
                {
                    if (parent.ContainsKey(e.To))               { continue; }
                    if (e.Flow <= 0)                            { continue; }

                    parent[e.To] = e;
                    queue.Enqueue(e.To);
                }
            }

            if (!parent.ContainsKey(sink)) { break; }

            /* reconstruct & peel */
            var path = new List<SimpleEdge>();
            int node = sink;
            long minFlow = long.MaxValue;

            while (node != source)
            {
                var edge = parent[node];
                path.Add(edge);
                minFlow = Math.Min(minFlow, edge.Flow);
                node = edge.From;
            }

            path.Reverse();
            paths.Add(path);

            foreach (SimpleEdge e in path)
            {
                e.Flow -= minFlow;
            }
        }

        return paths;
    }
}