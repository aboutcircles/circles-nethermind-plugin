namespace Circles.Pathfinder.Edges;

/// <summary>
/// Represents a flow edge for actual token transfers between nodes.
/// </summary>
public class FlowEdge : CapacityEdge
{
    public long CurrentCapacity { get; set; }
    public long Flow { get; set; }
    public FlowEdge? ReverseEdge { get; set; }

    public FlowEdge(int from, int to, int token, long initialCapacity)
        : base(from, to, token, initialCapacity)
    {
        CurrentCapacity = initialCapacity;
        Flow = 0;
    }
}