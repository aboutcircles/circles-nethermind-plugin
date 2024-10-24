using System.Numerics;

namespace Circles.Pathfinder.Nodes;

public class BalanceNode : Node
{
    public string Token { get; }
    public BigInteger Amount { get; }

    public string HolderAddress => Address.Split("-")[0];

    public BalanceNode(string address, string token, BigInteger amount) : base(address + "-" + token)
    {
        Token = token;
        Amount = amount;
    }
}