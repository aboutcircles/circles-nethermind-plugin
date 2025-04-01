using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Host.State;

public sealed class NetworkState
{
    // Single set of graphs
    public TrustGraph? TrustGraph => _trustGraph;
    private TrustGraph? _trustGraph;
    
    public BalanceGraph? BalanceGraph => _balanceGraph;
    private BalanceGraph? _balanceGraph;

    internal void Replace(
        TrustGraph? trustGraph = null, 
        BalanceGraph? balanceGraph = null)
    {
        if (trustGraph != null)
            Interlocked.Exchange(ref _trustGraph, trustGraph);
            
        if (balanceGraph != null)
            Interlocked.Exchange(ref _balanceGraph, balanceGraph);
    }
}