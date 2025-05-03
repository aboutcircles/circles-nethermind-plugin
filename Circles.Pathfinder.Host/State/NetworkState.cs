using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Host.State;

public sealed class NetworkState
{
    public IReadOnlyDictionary<int, HashSet<int>> AccountTrusts => _accountTrusts;
    private Dictionary<int, HashSet<int>> _accountTrusts = new();
    
    public BalanceGraph? BalanceGraph => _balanceGraph;
    private BalanceGraph? _balanceGraph;

    internal void Replace(
        BalanceGraph?                      balanceGraph     = null,
        Dictionary<int, HashSet<int>>?     accountTrusts    = null)
    {
        if (balanceGraph is not null)
        {
            Interlocked.Exchange(ref _balanceGraph, balanceGraph);
        }

        if (accountTrusts is not null)
        {
            Interlocked.Exchange(ref _accountTrusts, accountTrusts);
        }
    }
}