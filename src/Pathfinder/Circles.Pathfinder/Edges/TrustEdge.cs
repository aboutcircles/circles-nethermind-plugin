namespace Circles.Pathfinder.Edges;

/// <summary>
/// Represents a trust relationship between two nodes.
/// </summary>
public class TrustEdge : Edge
{
    public TrustEdge(int from, int to) : base(from, to)
    {
    }
}
