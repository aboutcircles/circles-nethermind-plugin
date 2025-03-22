using Nethermind.Int256;

namespace Circles.Pathfinder.Edges;

/// <summary>
/// Represents a capacity edge for potential token transfers between nodes.
/// </summary>
public class CapacityEdge : Edge
{
    public string Token { get; }
    public UInt256 InitialCapacity { get; }

    public CapacityEdge(string from, string to, string token, UInt256 initialCapacity) : base(from, to)
    {
        Token = token;
        InitialCapacity = initialCapacity;
    }
}