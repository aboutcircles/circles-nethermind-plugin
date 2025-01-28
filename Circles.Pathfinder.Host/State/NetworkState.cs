using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Host.State;

public sealed class NetworkState
{
    public TrustGraph? TrustGraph => _trustGraph;
    private TrustGraph? _trustGraph;

    public BalanceGraph? BalanceGraph => _balanceGraph;
    private BalanceGraph? _balanceGraph;

    internal void Replace(TrustGraph? trustGraph, BalanceGraph? balanceGraph)
    {
        Interlocked.Exchange(ref _trustGraph, trustGraph);
        Interlocked.Exchange(ref _balanceGraph, balanceGraph);
    }
}
