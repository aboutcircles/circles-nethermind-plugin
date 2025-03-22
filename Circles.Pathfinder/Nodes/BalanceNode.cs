using Nethermind.Int256;

namespace Circles.Pathfinder.Nodes;

public class BalanceNode : Node
{
    public string Token { get; }
    public UInt256 Amount { get; }

    public string HolderAddress => Address.Split("-")[0];

    public BalanceNode(string address, string token, UInt256 amount) : base(address + "-" + token)
    {
        Token = token;
        Amount = amount;
    }
}