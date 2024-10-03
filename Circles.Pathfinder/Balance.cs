using System.Numerics;

namespace Circles.Pathfinder;

public class Balance
{
    public string Account { get; set; }
    public string TokenAddress { get; set; }
    public int AccountId { get; set; }
    public int TokenId { get; set; }
    public BigInteger DemurragedTotalBalance { get; set; }
}