using Nethermind.Int256;

namespace Circles.Pathfinder.Nodes;

public class BalanceNode : Node
{
    public string Token { get; }
    public long Amount { get; }

    public string HolderAddress => Address.Split("-")[0];

    public BalanceNode(string address, string token, long amount) : base(address + "-" + token)
    {
        Token = token;
        Amount = amount;
    }
}