using Circles.Common.Dto;
using Circles.Pathfinder.Data;
using Microsoft.Extensions.Logging;

namespace Circles.Pathfinder.Graphs;

public sealed class CapacityGraphPool(string routerAddress, ILoadGraph loadGraph, ILogger<GraphFactory>? graphFactoryLogger = null)
{
    private volatile CapacityGraphSnapshot? _current;
    private volatile CapacityGraphSnapshot? _currentWrapped;
    private volatile CachedGroupData? _cachedGroupData;
    private readonly GraphFactory _gf = new(routerAddress, loadGraph, graphFactoryLogger);

    /// <summary>Whether a snapshot has been loaded (used by health checks).</summary>
    public bool HasCurrentSnapshot => _current is not null;

    /// <summary>Current snapshot (for metrics). May be null before first load.</summary>
    public CapacityGraphSnapshot? CurrentSnapshot => _current;

    /// <summary>Current wrapped snapshot (for metrics). May be null before first load.</summary>
    public CapacityGraphSnapshot? CurrentWrappedSnapshot => _currentWrapped;

    /* ------------------------------------------------------------------ */
    /* Snapshot                                                           */
    /* ------------------------------------------------------------------ */

    public void UpdateSnapshot(CapacityGraphSnapshot snap, CachedGroupData? groupData = null)
    {
        // Store cached group data before snapshot so readers see it when they see the new snapshot
        _cachedGroupData = groupData;
        // Volatile write (field is already volatile) — old snapshot becomes
        // eligible for GC once all in-flight requests release their references.
        _current = snap;
    }

    /// <summary>
    /// Updates the pre-built wrapped snapshot. Called by the background service
    /// after building the base snapshot, so withWrap=true requests hit the cache.
    /// </summary>
    public void UpdateWrappedSnapshot(CapacityGraphSnapshot snap)
    {
        _currentWrapped = snap;
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
            // Fast path: if ONLY withWrap is set (no other filters), use pre-built wrapped snapshot
            if (IsWrapOnly(r))
            {
                var wrappedSnap = _currentWrapped;
                if (wrappedSnap != null)
                {
                    return Task.FromResult(new CapacityGraphHandle(wrappedSnap.Base));
                }
                // Fall through to ad-hoc build if wrapped snapshot not yet available
            }

            // build ad-hoc filtered graph, using cached group/consent data to skip DB queries
            var g = _gf.CreateCapacityGraph(balances, trust, r, _cachedGroupData);
            g.Block = snap.Block; // propagate block for replay logging
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
        ILoadGraph loadGraph,
        string routerAddress)
    {
        var gf = new GraphFactory(routerAddress, loadGraph);
        return Task.FromResult(gf.CreateBaseCapacityGraph(balanceGraph, accountTrusts));
    }

    /// <summary>
    /// Builds a full graph with withWrap=true applied (all wrapped edges included).
    /// Used by the background service to pre-build the wrapped snapshot.
    /// </summary>
    public static Task<CapacityGraph> BuildFullWrappedGraph(
        BalanceGraph balanceGraph,
        IReadOnlyDictionary<int, HashSet<int>> accountTrusts,
        ILoadGraph loadGraph,
        string routerAddress,
        CachedGroupData? cachedGroupData = null)
    {
        var gf = new GraphFactory(routerAddress, loadGraph);
        // Build a filtered graph with only WithWrap=true set
        var wrapRequest = new FlowRequest { WithWrap = true };
        return Task.FromResult(gf.CreateCapacityGraph(balanceGraph, accountTrusts, wrapRequest, cachedGroupData));
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

    /// <summary>
    /// Returns true when the ONLY reason a request needs filtering is WithWrap=true.
    /// DISABLED: The pre-built wrapped snapshot lacks source-specific wrapped supply edges.
    /// Line 733 of AddHolderToTokenEdges_Pooled skips all wrapped balances when sourceId
    /// is null (pre-build mode), because only the source can supply wrapped tokens (the
    /// caller initiates the ERC20 unwrap). This means the pre-built snapshot has zero
    /// wrapped supply edges, causing maxFlow=0 for users whose balance is in wrappers.
    /// Always return false to force ad-hoc graph building with proper source context.
    /// </summary>
    public static bool IsWrapOnly(FlowRequest r) => false;
}

public sealed class CapacityGraphSnapshot
{
    public long Block { get; }
    public CapacityGraph Base { get; }

    public CapacityGraphSnapshot(long block, CapacityGraph @base)
    {
        Block = block;
        Base = @base;
        // Stamp block on the graph so V2Pathfinder can log it without access to the snapshot
        @base.Block = block;
    }
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
