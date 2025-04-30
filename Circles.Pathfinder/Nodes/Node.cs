using Circles.Pathfinder.Edges;

namespace Circles.Pathfinder.Nodes;

public abstract class Node(int address)
{
    public int Address { get; set; } = address;
    public List<Edge> OutEdges { get; } = new();
    public List<Edge> InEdges { get; } = new();
}