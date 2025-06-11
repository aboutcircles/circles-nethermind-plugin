using System.Collections.Concurrent;
using Circles.Pathfinder.DTOs;

namespace Circles.Pathfinder.Graphs;

public sealed class CapacityGraphPool
{
    private readonly ConcurrentDictionary<CapacityGraphSnapshot, int> _ref = new();
    private volatile CapacityGraphSnapshot? _current;
    private readonly GraphFactory _gf = new();

    /* ------------------------------------------------------------------ */
    /* Snapshot                                                           */
    /* ------------------------------------------------------------------ */

    public void UpdateSnapshot(CapacityGraphSnapshot snap)
    {
        var old = _current;
        _current = snap;

        _ref.TryAdd(snap, 0);
        if (old != null && _ref.TryGetValue(old, out var cnt) && cnt == 0)
            _ref.TryRemove(old, out _);
    }

    /* ------------------------------------------------------------------ */
    /* Renting                                                            */
    /* ------------------------------------------------------------------ */

    public async Task<CapacityGraphHandle> Rent(FlowRequest r,
        BalanceGraph balances,
        IReadOnlyDictionary<int, HashSet<int>> trust)
    {
        if (_current == null)
            throw new InvalidOperationException("No capacity graph available yet.");

        if (RequestNeedsFiltering(r))
        {
            // build ad-hoc filtered graph
            var g = _gf.CreateCapacityGraphs(balances, trust, r);
            return new CapacityGraphHandle(g, null, this);
        }

        var snap = _current;
        _ref.AddOrUpdate(snap, _ => 1, (_, c) => c + 1);
        return new CapacityGraphHandle(snap.Base, snap, this);
    }

    internal void Release(CapacityGraphSnapshot snap)
    {
        if (!_ref.TryGetValue(snap, out var c)) return;
        var nc = _ref.AddOrUpdate(snap, _ => 0, (_, x) => x - 1);
        if (nc == 0 && snap != _current)
            _ref.TryRemove(snap, out _);
    }

    /* ------------------------------------------------------------------ */
    /* One-off builder used by the background service                     */
    /* ------------------------------------------------------------------ */

    public static async Task<CapacityGraphs> BuildFullGraph(
        BalanceGraph balanceGraph,
        IReadOnlyDictionary<int, HashSet<int>> accountTrusts)
    {
        var gf = new GraphFactory();
        return gf.CreateCapacityGraphs(balanceGraph, accountTrusts, new FlowRequest());
    }

    public static bool RequestNeedsFiltering(FlowRequest r)
    {
        bool hasIncludeFilters =
            (r.FromTokens?.Any() ?? false) ||
            (r.ToTokens?.Any() ?? false);

        bool hasExcludeFilters =
            (r.ExcludedFromTokens?.Any() ?? false) ||
            (r.ExcludedToTokens?.Any() ?? false);

        bool hasWrap = r.WithWrap == true;

        bool needsFiltering = hasIncludeFilters || hasExcludeFilters || hasWrap;
        return needsFiltering;
    }
}

public sealed class CapacityGraphSnapshot(long block, CapacityGraphs @base)
{
    public long Block { get; } = block;
    public CapacityGraphs Base { get; } = @base;
}

public readonly struct CapacityGraphHandle(
    CapacityGraphs g,
    CapacityGraphSnapshot? s,
    CapacityGraphPool pool) : IDisposable
{
    public CapacityGraphs Graphs { get; } = g;

    public void Dispose()
    {
        if (s != null) pool.Release(s);
    }
}