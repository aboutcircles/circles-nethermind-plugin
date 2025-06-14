namespace Circles.Index.Common;

public class BalanceNode(int balanceNodeId, int holder, int token, long amount, bool isWrapped, bool isStatic)
    : Node(balanceNodeId)
{
    public int Holder { get; } = holder;
    public int Token { get; } = token;
    public long Amount { get; } = amount;
    public bool IsWrapped { get; } = isWrapped;
    public bool IsStatic { get; } = isStatic;
}