namespace Circles.Pathfinder.Edges;

public abstract class Edge
{
    public int From { get; }
    public int To { get; }

    public Edge(int from, int to)
    {
        From = from;
        To = to;
    }
}