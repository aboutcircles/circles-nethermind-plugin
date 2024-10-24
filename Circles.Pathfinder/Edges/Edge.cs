namespace Circles.Pathfinder.Edges;

public abstract class Edge
{
    public string From { get; }
    public string To { get; }

    public Edge(string from, string to)
    {
        From = from;
        To = to;
    }
}