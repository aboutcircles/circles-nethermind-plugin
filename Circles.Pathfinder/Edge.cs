using System.Numerics;

namespace Circles.Pathfinder;

public class Edge
{
    public int From { get; set; }
    public int To { get; set; }
    public BigInteger Capacity { get; set; }
    public BigInteger Flow { get; set; }
    public int TokenId { get; set; }

    public Edge(int from, int to, BigInteger capacity, int tokenId)
    {
        From = from;
        To = to;
        Capacity = capacity;
        TokenId = tokenId;
        Flow = BigInteger.Zero;
    }
}