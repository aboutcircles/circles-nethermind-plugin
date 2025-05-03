namespace Circles.Pathfinder.Graphs;

/// <summary>
/// Wraps a per-request clone of the graph and tracks when it can be released.
/// </summary>
public readonly struct FlowGraphHandle : IDisposable
{
    public FlowGraph Graph { get; }

    private readonly FlowGraphSnapshot? _snapshot;   // null  ⇒  ad-hoc filtered graph
    private readonly FlowGraphPool _pool;

    internal FlowGraphHandle(
        FlowGraph graph,
        FlowGraphSnapshot? snapshot,
        FlowGraphPool pool)
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _snapshot = snapshot;
        _pool = pool;
    }

    public void Dispose()
    {
        if (_snapshot != null)
        {
            _pool.Release(_snapshot);
        }
    }
}
