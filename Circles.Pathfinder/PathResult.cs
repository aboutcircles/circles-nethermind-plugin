using System.Numerics;

namespace Circles.Pathfinder;

public class PathResult
{
    public string Source { get; set; }
    public string Sink { get; set; }
    public BigInteger MaxTransferableAmount { get; set; }
    public List<Transfer> Transfers { get; set; }
}