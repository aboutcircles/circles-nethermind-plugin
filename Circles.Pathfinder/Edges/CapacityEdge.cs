using System.Numerics;

namespace Circles.Pathfinder.Edges;

/// <summary>
/// Represents a capacity edge for potential token transfers between nodes.
/// </summary>
public class CapacityEdge : Edge
{
    public string Token { get; }
    public BigInteger InitialCapacity { get; }

    public CapacityEdge(string from, string to, string token, BigInteger initialCapacity) : base(from, to)
    {
        Token = token;
        InitialCapacity = initialCapacity;
    }
}