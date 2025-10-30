namespace Circles.Pathfinder.Edges;

/// <summary>
/// Represents a capacity edge for potential token transfers between nodes.
/// </summary>
public class CapacityEdge : Edge
{
    public int Token { get; }
    public long InitialCapacity { get; }

    public CapacityEdge(int from, int to, int token, long initialCapacity) : base(from, to)
    {
        Token = token;
        InitialCapacity = initialCapacity;
    }
}