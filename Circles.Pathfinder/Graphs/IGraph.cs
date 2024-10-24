using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Graphs;

public interface IGraph<TEdge>
    where TEdge : Edge
{
    IDictionary<string, Node> Nodes { get; }
    HashSet<TEdge> Edges { get; }
}