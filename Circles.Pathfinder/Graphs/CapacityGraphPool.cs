using System.Collections.Concurrent;
using Circles.Index.Common;
using Circles.Pathfinder.Data; 
using Circles.Pathfinder.DTOs;

namespace Circles.Pathfinder.Graphs;

public sealed class CapacityGraphPool(Settings settings, LoadGraph loadGraph)
{
    private readonly ConcurrentDictionary<CapacityGraphSnapshot, int> _ref = new();
    private volatile CapacityGraphSnapshot? _current;
    private readonly GraphFactory _gf = new(settings.BaseGroupRouter, loadGraph);


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

    public Task<CapacityGraphHandle> Rent(FlowRequest r,
        BalanceGraph balances,
        IReadOnlyDictionary<int, HashSet<int>> trust)
    {
        if (_current == null)
            throw new InvalidOperationException("No capacity graph available yet.");

        if (RequestNeedsFiltering(r))
        {
            // build ad-hoc filtered graph
            var g = _gf.CreateCapacityGraph(balances, trust, r);
            return Task.FromResult(new CapacityGraphHandle(g, null, this));
        }

        var snap = _current;
        _ref.AddOrUpdate(snap, _ => 1, (_, c) => c + 1);
        return Task.FromResult(new CapacityGraphHandle(snap.Base, snap, this));
    }

    internal void Release(CapacityGraphSnapshot snap)
    {
        if (!_ref.TryGetValue(snap, out _)) return;
        var nc = _ref.AddOrUpdate(snap, _ => 0, (_, x) => x - 1);
        if (nc == 0 && snap != _current)
            _ref.TryRemove(snap, out _);
    }

    /* ------------------------------------------------------------------ */
    /* One-off builder used by the background service                     */
    /* ------------------------------------------------------------------ */

    public static Task<CapacityGraph> BuildFullGraph(
        BalanceGraph balanceGraph,
        IReadOnlyDictionary<int, HashSet<int>> accountTrusts,
        LoadGraph loadGraph,
        string routerAddress)
    {
        var gf = new GraphFactory(routerAddress, loadGraph);
        return Task.FromResult(gf.CreateCapacityGraph(balanceGraph, accountTrusts));
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
        bool hasSimulatedBalances = r.SimulatedBalances?.Any() ?? false;
        bool hasSimulatedTrusts = r.SimulatedTrusts?.Any() ?? false;

        bool needsFiltering = hasIncludeFilters
                              || hasExcludeFilters
                              || hasWrap
                              || hasSimulatedBalances
                              || hasSimulatedTrusts;

        return needsFiltering;
    }
}

public sealed class CapacityGraphSnapshot(long block, CapacityGraph @base)
{
    public long Block { get; } = block;
    public CapacityGraph Base { get; } = @base;
}

public readonly struct CapacityGraphHandle(
    CapacityGraph g,
    CapacityGraphSnapshot? s,
    CapacityGraphPool pool) : IDisposable
{
    public CapacityGraph Graph { get; } = g;

    public void Dispose()
    {
        if (s != null) pool.Release(s);
    }
}