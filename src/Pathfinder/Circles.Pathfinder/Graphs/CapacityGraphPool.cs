using Circles.Pathfinder.Data;
using Circles.Common.Dto;
using Microsoft.Extensions.Logging;

namespace Circles.Pathfinder.Graphs;

public sealed class CapacityGraphPool(string routerAddress, LoadGraph loadGraph, ILogger<GraphFactory>? graphFactoryLogger = null)
{
    private volatile CapacityGraphSnapshot? _current;
    private readonly GraphFactory _gf = new(routerAddress, loadGraph, graphFactoryLogger);

    /// <summary>Whether a snapshot has been loaded (used by health checks).</summary>
    public bool HasCurrentSnapshot => _current is not null;

    /* ------------------------------------------------------------------ */
    /* Snapshot                                                           */
    /* ------------------------------------------------------------------ */

    public void UpdateSnapshot(CapacityGraphSnapshot snap)
    {
        // Volatile write (field is already volatile) — old snapshot becomes
        // eligible for GC once all in-flight requests release their references.
        _current = snap;
    }

    /* ------------------------------------------------------------------ */
    /* Renting                                                            */
    /* ------------------------------------------------------------------ */

    public Task<CapacityGraphHandle> Rent(FlowRequest r,
        BalanceGraph balances,
        IReadOnlyDictionary<int, HashSet<int>> trust)
    {
        var snap = _current;
        if (snap == null)
            throw new InvalidOperationException("No capacity graph available yet.");

        if (RequestNeedsFiltering(r))
        {
            // build ad-hoc filtered graph
            var g = _gf.CreateCapacityGraph(balances, trust, r);
            return Task.FromResult(new CapacityGraphHandle(g));
        }

        // Return the shared snapshot — GC keeps it alive while referenced
        return Task.FromResult(new CapacityGraphHandle(snap.Base));
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
        bool hasSimulatedConsentedAvatars = r.SimulatedConsentedAvatars?.Any() ?? false;
        bool hasQuantizedMode = r.QuantizedMode == true;

        bool needsFiltering = hasIncludeFilters
                              || hasExcludeFilters
                              || hasWrap
                              || hasSimulatedBalances
                              || hasSimulatedTrusts
                              || hasSimulatedConsentedAvatars
                              || hasQuantizedMode;

        return needsFiltering;
    }
}

public sealed class CapacityGraphSnapshot(long block, CapacityGraph @base)
{
    public long Block { get; } = block;
    public CapacityGraph Base { get; } = @base;
}

/// <summary>
/// Lightweight handle returned by Rent. No-op Dispose — GC manages lifetime.
/// </summary>
public readonly struct CapacityGraphHandle(CapacityGraph g) : IDisposable
{
    public CapacityGraph Graph { get; } = g;

    public void Dispose()
    {
        // No-op: CapacityGraph is immutable after construction,
        // GC reclaims when no longer referenced.
    }
}
