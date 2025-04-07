namespace Circles.Pathfinder.Nodes;

public class BalanceNode(string address, string token, long amount, bool isWrapped, bool isStatic)
    : Node(address + "-" + token)
{
    public string Token { get; } = token;
    public long Amount { get; } = amount;
    public bool IsWrapped { get; } = isWrapped;
    public bool IsStatic { get; } = isStatic;

    public string HolderAddress => Address.Split("-")[0];
}