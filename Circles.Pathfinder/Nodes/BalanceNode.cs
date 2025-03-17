using System.Numerics;

namespace Circles.Pathfinder.Nodes;

public class BalanceNode : Node
{
    public string Token { get; }
    public BigInteger Amount { get; }
    public bool IsWrapped { get; }

    public string HolderAddress => Address.Split("-")[0];

    public BalanceNode(string address, string token, BigInteger amount, bool isWrapped = false) : base(address + "-" + token)
    {
        Token = token;
        Amount = amount;
        IsWrapped = isWrapped;
    }
}