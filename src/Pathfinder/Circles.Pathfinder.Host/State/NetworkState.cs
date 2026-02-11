using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Host.State;

/// <summary>
/// Immutable snapshot of all graph state. A single Volatile.Write swap
/// ensures readers never see mismatched trust/balance data.
/// </summary>
public sealed record GraphState(
    BalanceGraph? BalanceGraph,
    IReadOnlyDictionary<int, HashSet<int>> AccountTrusts,
    long Block,
    DateTime UpdateTime);

public sealed class NetworkState
{
    private volatile GraphState _state = new(null, new Dictionary<int, HashSet<int>>(), 0L, default);

    /// <summary>Current immutable snapshot — a single read yields a consistent view.</summary>
    public GraphState Current => _state;

    // Convenience accessors (delegate to snapshot)
    public BalanceGraph? BalanceGraph => _state.BalanceGraph;
    public IReadOnlyDictionary<int, HashSet<int>> AccountTrusts => _state.AccountTrusts;
    public long LastKnownBlockNumber => _state.Block;
    public DateTime LastUpdateTime => _state.UpdateTime;

    /// <summary>
    /// Atomically replace state fields. Any non-null parameter replaces the
    /// corresponding field; null parameters keep the existing value.
    /// Uses a CAS spin loop so concurrent partial updates are safe.
    /// </summary>
    internal void Replace(
        BalanceGraph? balanceGraph = null,
        Dictionary<int, HashSet<int>>? accountTrusts = null,
        long? lastKnownBlockNumber = null)
    {
        // CAS loop for safe concurrent partial updates
        GraphState snapshot;
        GraphState updated;
        do
        {
            snapshot = _state;
            updated = new GraphState(
                balanceGraph ?? snapshot.BalanceGraph,
                accountTrusts ?? (IReadOnlyDictionary<int, HashSet<int>>)snapshot.AccountTrusts,
                lastKnownBlockNumber ?? snapshot.Block,
                DateTime.UtcNow);
        }
        while (Interlocked.CompareExchange(ref _state, updated, snapshot) != snapshot);
    }
}
