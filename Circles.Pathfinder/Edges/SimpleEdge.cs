namespace Circles.Pathfinder.Edges;

internal sealed record SimpleEdge(int From, int To, int Token, long Capacity)
{
    public long Flow { get; set; }
}