using System.Numerics;
using Nethermind.Int256;

namespace Circles.Pathfinder.Edges;

/// <summary>
/// Represents a flow edge for actual token transfers between nodes.
/// </summary>
public class FlowEdge : CapacityEdge
{
    public UInt256 CurrentCapacity { get; set; }
    public UInt256 Flow { get; set; }
    public FlowEdge? ReverseEdge { get; set; }

    public FlowEdge(string from, string to, string token, UInt256 initialCapacity)
        : base(from, to, token, initialCapacity)
    {
        CurrentCapacity = initialCapacity;
        Flow = 0;
    }
}