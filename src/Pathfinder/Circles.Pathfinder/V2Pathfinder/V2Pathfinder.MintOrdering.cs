using Circles.Common;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Microsoft.Extensions.Logging;

namespace Circles.Pathfinder;

public partial class V2Pathfinder
{
    /* ------------------------------------------------------------------------
     * Sort edges to ensure mint dependencies are satisfied.
     *
     * Contract constraint: Groups need to receive ALL collateral BEFORE they
     * can transfer group tokens. The operateFlowMatrix processes edges
     * sequentially, so we must order them correctly.
     *
     * Ordering rules:
     * 1. All Avatar → Router edges (collateral handoff to router)
     * 2. For each group: All Router → Group edges (collateral deposit)
     * 3. For each group: All Group → Avatar edges (group token minting)
     * 4. All other edges (standard avatar-to-avatar transfers)
     *
     * This ensures that when a group's outbound edge is processed, it has
     * already received all the collateral it needs.
     * --------------------------------------------------------------------- */
    internal static List<FlowEdge> SortEdgesForMintDependencies(List<FlowEdge> edges, CapacityGraph capacityGraph)
    {
        if (capacityGraph.RouterNode == null || capacityGraph.GroupNodes.Count == 0)
        {
            // No router or no groups - nothing to sort
            return edges;
        }

        // Categorize edges into buckets
        var avatarToRouter = new List<FlowEdge>();
        var groupEdges = new Dictionary<int, (List<FlowEdge> Inbound, List<FlowEdge> Outbound)>();
        var otherEdges = new List<FlowEdge>();

        foreach (var edge in edges)
        {
            bool fromIsRouter = capacityGraph.IsRouter(edge.From);
            bool toIsRouter = capacityGraph.IsRouter(edge.To);
            bool fromIsGroup = capacityGraph.IsGroup(edge.From);
            bool toIsGroup = capacityGraph.IsGroup(edge.To);

            if (!fromIsRouter && !fromIsGroup && toIsRouter)
            {
                // Avatar → Router (collateral sent to router)
                avatarToRouter.Add(edge);
            }
            else if (fromIsRouter && toIsGroup)
            {
                // Router → Group (collateral deposited to group)
                int groupId = edge.To;
                if (!groupEdges.TryGetValue(groupId, out var lists))
                {
                    lists = (new List<FlowEdge>(), new List<FlowEdge>());
                    groupEdges[groupId] = lists;
                }
                lists.Inbound.Add(edge);
            }
            else if (fromIsGroup && !toIsGroup && !toIsRouter)
            {
                // Group → Avatar (group token minting)
                int groupId = edge.From;
                if (!groupEdges.TryGetValue(groupId, out var lists))
                {
                    lists = (new List<FlowEdge>(), new List<FlowEdge>());
                    groupEdges[groupId] = lists;
                }
                lists.Outbound.Add(edge);
            }
            else
            {
                // All other edges (including direct Avatar → Avatar transfers)
                otherEdges.Add(edge);
            }
        }

        // Build result in dependency order
        var result = new List<FlowEdge>(edges.Count);

        // 1. All Avatar → Router edges first
        result.AddRange(avatarToRouter);

        // 2. For each group: all inbound (Router → Group) before outbound (Group → Avatar)
        foreach (var (groupId, (inbound, outbound)) in groupEdges)
        {
            result.AddRange(inbound);
            result.AddRange(outbound);
        }

        // 3. Other edges last
        result.AddRange(otherEdges);

        return result;
    }

    /* ------------------------------------------------------------------------
     * Validate that edge ordering satisfies mint dependency constraints.
     * Throws InvalidOperationException if ordering is violated.
     *
     * Invariants checked:
     * 1. For each group G, all Router → G edges must appear BEFORE any G → Avatar edge
     * 2. When a Group → Avatar edge is seen, the cumulative inbound flow must be
     *    >= the cumulative outbound flow required so far
     *
     * This validation ensures the contract won't revert with ERC1155InsufficientBalance.
     * --------------------------------------------------------------------- */
    private void ValidateMintEdgeOrdering(PipelineContext ctx)
    {
        var error = ValidateMintEdgeOrdering(ctx.Edges, ctx.Graph);
        if (error != null)
        {
            _logger.LogError("[{ReqId}] {Error}", ctx.ReqId, error);
            ctx.Edges.Clear();
        }
    }

    internal static string? ValidateMintEdgeOrdering(List<FlowEdge> edges, CapacityGraph capacityGraph)
    {
        if (capacityGraph.RouterNode == null || capacityGraph.GroupNodes.Count == 0)
        {
            // No router or no groups - nothing to validate
            return null;
        }

        // Track which groups have had their outbound edge seen
        var groupsWithOutboundSeen = new HashSet<int>();

        // Track cumulative inbound flow per group
        var groupInboundFlow = new Dictionary<int, long>();

        // Track cumulative outbound flow per group
        var groupOutboundFlow = new Dictionary<int, long>();

        for (int i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            bool fromIsRouter = capacityGraph.IsRouter(edge.From);
            bool fromIsGroup = capacityGraph.IsGroup(edge.From);
            bool toIsGroup = capacityGraph.IsGroup(edge.To);

            if (fromIsRouter && toIsGroup)
            {
                // Router → Group (inbound to group)
                int groupId = edge.To;

                // Violation: We've already seen an outbound from this group
                // but we're now seeing more inbound - ordering is wrong
                if (groupsWithOutboundSeen.Contains(groupId))
                {
                    string groupAddr = AddressIdPool.StringOf(groupId);
                    return $"Edge ordering violation: Router → Group edge for group {groupAddr} " +
                        $"appears after Group → Avatar edge at index {i}. " +
                        "All collateral must be deposited before minting.";
                }

                groupInboundFlow.TryGetValue(groupId, out long current);
                groupInboundFlow[groupId] = current + edge.Flow;
            }
            else if (fromIsGroup && !capacityGraph.IsRouter(edge.To) && !capacityGraph.IsGroup(edge.To))
            {
                // Group → Avatar (outbound from group - minting)
                int groupId = edge.From;
                groupsWithOutboundSeen.Add(groupId);

                groupOutboundFlow.TryGetValue(groupId, out long currentOutbound);
                groupOutboundFlow[groupId] = currentOutbound + edge.Flow;

                // Check flow conservation: cumulative inbound >= cumulative outbound so far
                groupInboundFlow.TryGetValue(groupId, out long inbound);
                if (inbound < groupOutboundFlow[groupId])
                {
                    string groupAddr = AddressIdPool.StringOf(groupId);
                    return $"Flow violation: Group {groupAddr} has insufficient collateral at edge index {i}. " +
                        $"Cumulative inbound: {inbound}, cumulative outbound required: {groupOutboundFlow[groupId]}.";
                }
            }
        }

        return null;
    }
}
