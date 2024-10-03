using System.Numerics;

namespace Circles.Pathfinder;

public class Graph
{
    public int NodeCount { get; set; }
    public List<Edge>[] AdjacencyList { get; set; }

    public Graph(int nodeCount)
    {
        NodeCount = nodeCount;
        AdjacencyList = new List<Edge>[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            AdjacencyList[i] = new List<Edge>();
        }
    }

    public void AddEdge(int from, int to, BigInteger capacity, int tokenId)
    {
        var edge = new Edge(from, to, capacity, tokenId);
        AdjacencyList[from].Add(edge);
    }
}