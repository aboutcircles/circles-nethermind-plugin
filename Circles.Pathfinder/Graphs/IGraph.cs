using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Graphs;

public interface IGraph<TEdge>
    where TEdge : Edge
{
    IDictionary<int, Node> Nodes { get; }
    HashSet<TEdge> Edges { get; }
}