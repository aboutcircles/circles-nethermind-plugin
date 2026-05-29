using Circles.Common;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Microsoft.Extensions.Logging;

namespace Circles.Pathfinder;

public partial class V2Pathfinder
{
    /* ------------------------------------------------------------------------
     * Post-process: Insert Router node between Avatar → Group transfers.
     *
     * Mirrors Hub.sol:904-912 _effectPathTransfers() which routes group mints:
     *
     *   if (mintGroups.get(to)) {
     *       _groupMint(
     *           _flowVertices[_coordinates[index + 1]], // sender = "from" of flow edge
     *           to,                                      // receiver = group
     *           _flow.streams[index].circles,
     *           _flow.streams[index].ids[...]
     *       );
     *   }
     *
     * The Router is the intermediary that holds collateral tokens before
     * depositing them to groups. The contract's operateFlowMatrix expects
     * all Avatar→Group transfers to go through the Router.
     *
     * Consent enforcement is handled at the path level (PathHasConsentViolation /
     * PathHasConsentedIntermediary in CollapseBalanceNodes) BEFORE this rewrite.
     * Both filters reject consented sender → Group unconditionally because, after
     * Router insertion, Avatar(consented)→Router would always revert on-chain
     * (Hub.sol's isPermittedFlow requires advancedUsageFlags[to] != 0 for
     * consented senders, but the Router contract never calls setAdvancedUsageFlag).
     * --------------------------------------------------------------------- */
    internal List<FlowEdge> InsertRouterInTransfers(List<FlowEdge> transfers, CapacityGraph capacityGraph)
    {
        if (capacityGraph.RouterNode == null)
            return transfers;

        var result = new List<FlowEdge>();

        foreach (var transfer in transfers)
        {
            // If this is Avatar → Group transfer, insert Router in between
            // (Group → Avatar minting transfers are left as-is)
            if (!capacityGraph.IsGroup(transfer.From) &&
                !capacityGraph.IsRouter(transfer.From) &&
                capacityGraph.IsGroup(transfer.To))
            {
                var routerId = capacityGraph.RouterForGroup(transfer.To);

                // Split into Avatar → Router → Group
                // Avatar → Router (same token)
                result.Add(new FlowEdge(transfer.From, routerId, transfer.Token, transfer.InitialCapacity)
                {
                    Flow = transfer.Flow,
                    CurrentCapacity = transfer.CurrentCapacity
                });

                // Router → Group (same token)
                result.Add(new FlowEdge(routerId, transfer.To, transfer.Token, transfer.InitialCapacity)
                {
                    Flow = transfer.Flow,
                    CurrentCapacity = transfer.CurrentCapacity
                });
            }
            else
            {
                // Keep as-is (includes Group → Avatar minting transfers)
                result.Add(transfer);
            }
        }

        return result;
    }

    /* ------------------------------------------------------------------------
     * Collapse balance nodes and token pools in a set of paths.
     *
     * IMPORTANT: Consent filtering is done here at the PATH level, BEFORE
     * aggregation. Each solver path is an independent Source→Sink flow.
     * Dropping an entire path preserves flow conservation by construction.
     *
     * Previous approach filtered individual edges AFTER aggregation, which
     * left "holes" — intermediate vertices with unbalanced in/out flows.
     * --------------------------------------------------------------------- */
    private (FlowGraph Graph, int ConsentDroppedPaths) CollapseBalanceNodes(
        List<List<FlowEdge>> pathsWithFlow, CapacityGraph capacityGraph,
        int sourceId, int sinkId)
    {
        var collapsed = new FlowGraph();

        /* ---------------- copy avatars that actually appear ---------------- */
        var avatarSet = new HashSet<int>();

        foreach (var path in pathsWithFlow)
        {
            foreach (var edge in path)
            {
                if (!IsPoolNode(edge.From))
                    avatarSet.Add(edge.From);

                if (!IsPoolNode(edge.To))
                    avatarSet.Add(edge.To);
            }
        }

        foreach (int a in avatarSet)
        {
            collapsed.AddAvatar(a);
        }

        /* ---- collapse per-path, consent-check, drop invalid, aggregate --- */
        var agg = new Dictionary<(int From, int To, int Token), long>();
        int droppedPaths = 0;

        foreach (var path in pathsWithFlow)
        {
            var pathEdges = CollapseSinglePathToEdges(path, capacityGraph);

            if (_settings.ExcludeConsentedIntermediaries)
            {
                // Conservative: exclude paths through consented intermediaries entirely
                if (PathHasConsentedIntermediary(pathEdges, capacityGraph, sourceId, sinkId))
                {
                    droppedPaths++;
                    continue;
                }
            }
            else
            {
                // Normal: apply consent rule validation (isPermittedFlow logic)
                if (PathHasConsentViolation(pathEdges, capacityGraph))
                {
                    droppedPaths++;
                    continue; // Drop entire path — preserves flow conservation
                }
            }

            // Aggregate surviving path edges
            foreach (var (from, to, token, flow) in pathEdges)
            {
                AddToAggregation(agg, from, to, token, flow);
            }
        }

        if (droppedPaths > 0)
        {
            if (droppedPaths == pathsWithFlow.Count)
            {
                _logger.LogWarning(
                    "[CollapseBalanceNodes] ALL {TotalPaths} paths dropped due to consent {Mode} — result will be zero flow. " +
                    "Source or intermediaries may have advancedUsageFlags preventing group minting paths.",
                    pathsWithFlow.Count,
                    _settings.ExcludeConsentedIntermediaries ? "intermediary exclusion" : "violations");
            }
            else
            {
                _logger.LogInformation("[CollapseBalanceNodes] Dropped {DroppedPaths}/{TotalPaths} paths due to consent {Mode}",
                    droppedPaths, pathsWithFlow.Count,
                    _settings.ExcludeConsentedIntermediaries ? "intermediary exclusion" : "violations");
            }
        }

        /* ---------------- materialise collapsed edges ---------------------- */
        foreach (var kvp in agg)
        {
            long flow = kvp.Value;
            if (flow <= 0)
            {
                continue;
            }

            var (from, to, token) = kvp.Key;
            var e = new FlowEdge(from, to, token, long.MaxValue)
            {
                Flow = flow,
                CurrentCapacity = long.MaxValue - flow
            };
            collapsed.Edges.Add(e);
        }

        return (collapsed, droppedPaths);
    }

    /* ------------------------------------------------------------------------
     * Collapse a single path into a list of (From, To, Token, Flow) tuples.
     * Like the old CollapseSinglePath but returns edges instead of aggregating
     * into a shared dictionary — needed for per-path consent checking.
     * --------------------------------------------------------------------- */
    internal List<(int From, int To, int Token, long Flow)> CollapseSinglePathToEdges(
        List<FlowEdge> path,
        CapacityGraph capacityGraph)
    {
        var edges = new List<(int From, int To, int Token, long Flow)>(path.Count);
        int i = 0;

        while (i < path.Count)
        {
            var e = path[i];

            // Case 1: Standard TokenPool collapse (Avatar → TokenPool → Avatar/Group)
            if (IsPoolNode(e.To))
            {
                bool hasNext = (i + 1) < path.Count;
                if (hasNext && path[i + 1].From == e.To)
                {
                    var next = path[i + 1];
                    edges.Add((e.From, next.To, e.Token, Math.Min(e.Flow, next.Flow)));
                    i += 2;
                    continue;
                }
            }

            // Case 2: Group → Avatar (group token minting, keep as-is)
            if (capacityGraph.IsGroup(e.From))
            {
                edges.Add((e.From, e.To, e.Token, e.Flow));
                i += 1;
                continue;
            }

            // Case 3: Any other direct edge (keep as-is)
            if (!IsPoolNode(e.To))
            {
                edges.Add((e.From, e.To, e.Token, e.Flow));
                i += 1;
                continue;
            }

            // Orphaned TokenPool edge
            _logger.LogWarning("[CollapseSinglePathToEdges] Orphaned TokenPool edge at index {Index}: {From}→{To} (token={Token}, flow={Flow})",
                i, AddressIdPool.StringOf(e.From)[..10], AddressIdPool.StringOf(e.To)[..10],
                AddressIdPool.StringOf(e.Token)[..10], e.Flow);
            i += 1;
        }

        return edges;
    }

    /* ------------------------------------------------------------------------
     * Check if a collapsed path has any consent violation.
     *
     * For each edge: if From is consented → check isTrusted(From, To) AND
     * ConsentedAvatars.Contains(To).
     *
     * Skip Avatar→Group edges (where To is a group) — these become
     * Avatar→Router→Group after InsertRouterInTransfers.
     * Group→Avatar (mint) edges stay as-is and must be consent-checked.
     * --------------------------------------------------------------------- */
    /// <summary>
    /// Checks if a collapsed path has any consent rule violation per Hub.sol isPermittedFlow.
    /// Consented sender→Group edges are not skipped (Router lacks advancedUsageFlags).
    /// </summary>
    internal bool PathHasConsentViolation(
        List<(int From, int To, int Token, long Flow)> collapsedEdges,
        CapacityGraph capacityGraph)
    {
        // No consent data → no violations possible
        if (capacityGraph.TrustLookup == null || capacityGraph.ConsentedAvatars.Count == 0)
            return false;

        foreach (var (from, to, _, _) in collapsedEdges)
        {
            // Consented sender → Group: UNCONDITIONAL violation.
            // After InsertRouterInTransfers, this becomes Avatar(consented) → Router → Group.
            // Hub.sol's isPermittedFlow(Avatar, Router, token) always fails because the
            // Router contract never calls setAdvancedUsageFlag — advancedUsageFlags[Router] == 0.
            // No exceptions, even if the Group itself is consented and trusted.
            if (capacityGraph.IsGroup(to) && capacityGraph.ConsentedAvatars.Contains(from))
                return true;

            // Non-consented sender → Group: safe — standard trust applies after Router insertion.
            if (capacityGraph.IsGroup(to))
                continue;

            // Skip pool nodes (shouldn't exist after collapse, but safety check)
            if (IsPoolNode(from) || IsPoolNode(to))
                continue;

            // If From doesn't have consented flow, standard trust is sufficient
            if (!capacityGraph.ConsentedAvatars.Contains(from))
                continue;

            // From has consented flow — check requirements:
            // 1. From must trust To
            bool fromTrustsTo = capacityGraph.TrustLookup.TryGetValue(from, out var fromTrusts)
                                && fromTrusts.Contains(to);
            if (!fromTrustsTo)
                return true; // Violation: consented avatar doesn't trust recipient

            // 2. To must also have consented flow enabled
            if (!capacityGraph.ConsentedAvatars.Contains(to))
                return true; // Violation: recipient doesn't have consented flow
        }

        return false;
    }

    /* ------------------------------------------------------------------------
     * Check if a collapsed path routes through any consented avatar as an
     * intermediary (i.e. NOT source or sink). Used when ExcludeConsentedIntermediaries
     * is true — instead of validating consent rules, we simply exclude
     * consented avatars from intermediary positions entirely.
     * --------------------------------------------------------------------- */
    /// <summary>
    /// Checks if a collapsed path routes through any consented avatar as an intermediary.
    /// Used in ExcludeConsentedIntermediaries mode as a conservative alternative to consent validation.
    /// </summary>
    internal bool PathHasConsentedIntermediary(
        List<(int From, int To, int Token, long Flow)> edges,
        CapacityGraph graph, int sourceId, int sinkId)
    {
        if (graph.ConsentedAvatars.Count == 0) return false;

        foreach (var (from, to, _, _) in edges)
        {
            // Consented sender → Group: UNCONDITIONAL exclusion (even if sender is source).
            // After InsertRouterInTransfers, this becomes Avatar(consented) → Router → Group.
            // Hub.sol's isPermittedFlow(Avatar, Router, token) always fails because the
            // Router contract never calls setAdvancedUsageFlag.
            if (graph.IsGroup(to) && graph.ConsentedAvatars.Contains(from))
                return true;

            // Non-consented sender → Group: safe — standard trust applies after Router insertion.
            if (graph.IsGroup(to))
                continue;

            // Consented sender → non-consented non-Group receiver: violates Hub.sol isPermittedFlow.
            // Hub.sol:668-676 requires advancedUsageFlags[to]==true when advancedUsageFlags[from]==true.
            // Mirrors the validation-mode check in PathHasConsentViolation.
            // Closes the consented-source → regular-human-intermediary case (prod 2026-04-28).
            if (graph.ConsentedAvatars.Contains(from) && !graph.ConsentedAvatars.Contains(to))
                return true;

            if (from != sourceId && from != sinkId && graph.ConsentedAvatars.Contains(from))
                return true;
            if (to != sourceId && to != sinkId && graph.ConsentedAvatars.Contains(to))
                return true;
        }

        return false;
    }

    private void AddToAggregation(
        Dictionary<(int From, int To, int Token), long> agg,
        int from,
        int to,
        int token,
        long flow)
    {
        var key = (from, to, token);
        if (agg.TryGetValue(key, out long existing))
        {
            // Saturating addition to prevent overflow (9.2e18 attoCircles ≈ 9.2 CRC)
            if (existing > long.MaxValue - flow)
            {
                _logger.LogWarning("[AddToAggregation] Saturated flow for edge {From}→{To}: existing={Existing}, flow={Flow}",
                    AddressIdPool.StringOf(from)[..10], AddressIdPool.StringOf(to)[..10], existing, flow);
                agg[key] = long.MaxValue;
            }
            else
            {
                agg[key] = existing + flow;
            }
        }
        else
        {
            agg[key] = flow;
        }
    }
}
