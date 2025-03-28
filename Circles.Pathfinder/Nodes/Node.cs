using Circles.Pathfinder.Edges;

namespace Circles.Pathfinder.Nodes;

public abstract class Node(string address)
{
    public string Address { get; set; } = address;
    public List<Edge> OutEdges { get; } = new();
    public List<Edge> InEdges { get; } = new();
}