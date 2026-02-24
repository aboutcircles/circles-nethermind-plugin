namespace Circles.Pathfinder.Nodes;

public class BalanceNode(int balanceNodeId, int holder, int token, long amount, bool isWrapped, bool isStatic)
    : Node(balanceNodeId)
{
    public int Holder { get; } = holder;
    public int Token { get; } = token;
    public long Amount { get; } = amount;
    public bool IsWrapped { get; } = isWrapped;
    // Tech debt: IsStatic is set but never read in production (LoadGraph always returns
    // demurraged balances). Only FixtureLoadGraph uses it to apply demurrage on static
    // fixtures. Removing it requires changing the BalanceNode constructor signature and
    // all call sites across ILoadGraph implementations.
    public bool IsStatic { get; } = isStatic;
}