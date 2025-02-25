using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Host.State;

public sealed class NetworkState
{
    // Regular graphs (without wrapped tokens)
    public TrustGraph? TrustGraph => _trustGraph;
    private TrustGraph? _trustGraph;
    
    public BalanceGraph? BalanceGraph => _balanceGraph;
    private BalanceGraph? _balanceGraph;
    
    // Wrapped graphs (with wrapped tokens)
    public TrustGraph? WrappedTrustGraph => _wrappedTrustGraph;
    private TrustGraph? _wrappedTrustGraph;
    
    public BalanceGraph? WrappedBalanceGraph => _wrappedBalanceGraph;
    private BalanceGraph? _wrappedBalanceGraph;

    internal void Replace(
        TrustGraph? trustGraph = null, 
        BalanceGraph? balanceGraph = null,
        TrustGraph? wrappedTrustGraph = null,
        BalanceGraph? wrappedBalanceGraph = null)
    {
        if (trustGraph != null)
            Interlocked.Exchange(ref _trustGraph, trustGraph);
            
        if (balanceGraph != null)
            Interlocked.Exchange(ref _balanceGraph, balanceGraph);
            
        if (wrappedTrustGraph != null)
            Interlocked.Exchange(ref _wrappedTrustGraph, wrappedTrustGraph);
            
        if (wrappedBalanceGraph != null)
            Interlocked.Exchange(ref _wrappedBalanceGraph, wrappedBalanceGraph);
    }
}