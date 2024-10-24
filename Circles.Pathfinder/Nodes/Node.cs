using Circles.Pathfinder.Edges;

namespace Circles.Pathfinder.Nodes;

public abstract class Node
{
    public string Address { get; set; }
    public List<Edge> OutEdges { get; } = new();
    public List<Edge> InEdges { get; } = new();

    protected Node(string address)
    {
        Address = address;
    }
}