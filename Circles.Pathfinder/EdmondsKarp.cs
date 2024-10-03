using System.Numerics;

namespace Circles.Pathfinder;

public class EdmondsKarp
{
    public static BigInteger MaxFlow(Graph? graph, int source, int sink, out Dictionary<int, Edge> parentMap)
    {
        BigInteger maxFlow = BigInteger.Zero;
        parentMap = new Dictionary<int, Edge>();

        while (true)
        {
            var flow = BFS(graph, source, sink, parentMap);
            if (flow.IsZero)
                break;
            maxFlow += flow;

            int current = sink;
            while (current != source)
            {
                var edge = parentMap[current];
                edge.Flow += flow;

                // Find the reverse edge
                var reverseEdge = graph.AdjacencyList[edge.To].FirstOrDefault(
                    e => e.To == edge.From && e.TokenId == edge.TokenId);
                if (reverseEdge == null)
                {
                    // Add reverse edge if it doesn't exist
                    reverseEdge = new Edge(edge.To, edge.From, BigInteger.Zero, edge.TokenId);
                    graph.AdjacencyList[edge.To].Add(reverseEdge);
                }
                reverseEdge.Flow -= flow;

                current = edge.From;
            }
        }

        return maxFlow;
    }

    private static BigInteger BFS(Graph? graph, int source, int sink, Dictionary<int, Edge> parentMap)
    {
        parentMap.Clear();
        var visited = new bool[graph.NodeCount];
        var queue = new Queue<int>();
        queue.Enqueue(source);
        visited[source] = true;

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            foreach (var edge in graph.AdjacencyList[current])
            {
                BigInteger residualCapacity = edge.Capacity - edge.Flow;
                if (residualCapacity > 0 && !visited[edge.To])
                {
                    visited[edge.To] = true;
                    parentMap[edge.To] = edge;
                    if (edge.To == sink)
                    {
                        return residualCapacity;
                    }
                    queue.Enqueue(edge.To);
                }
            }
        }
        return BigInteger.Zero;
    }
}
