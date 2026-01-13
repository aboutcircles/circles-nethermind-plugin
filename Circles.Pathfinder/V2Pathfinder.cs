using System.Globalization;
using Circles.Index.Common;
using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Nethermind.Int256;

namespace Circles.Pathfinder;

public class V2Pathfinder
{
    public long ComputeMaxFlow(
        CapacityGraph capacityGraph,
        FlowRequest flowRequest,
        UInt256 targetFlow)
    {
        /* --------------------------------------------------------------------
         * 1. Resolve ids and basic guards
         * ------------------------------------------------------------------ */
        int sinkId = AddressIdPool.IdOf(flowRequest.Sink);
        int? vs = capacityGraph.VirtualSinkAddress;
        int effSink = vs ?? sinkId;
        int sourceId = AddressIdPool.IdOf(flowRequest.Source);

        bool sourceMissing = !capacityGraph.AvatarNodes.ContainsKey(sourceId);
        if (sourceMissing)
        {
            throw new ArgumentException($"Source '{flowRequest.Source}' isn't in the graph snapshot.");
        }

        bool sinkMissing = !capacityGraph.AvatarNodes.ContainsKey(effSink);
        if (sinkMissing)
        {
            throw new ArgumentException($"Sink '{flowRequest.Sink}' isn't in the graph snapshot.");
        }

        /* --------------------------------------------------------------------
         * 2. Run max-flow (no path extraction needed)
         * ------------------------------------------------------------------ */
        long target = CirclesConverter.TruncateToInt64(targetFlow);
        var solved = MaxFlowSolver.Solve(
            capacityGraph.Edges,
            sourceId,
            effSink,
            target);

        /* --------------------------------------------------------------------
         * 3. Sum outbound flow from the source
         * ------------------------------------------------------------------ */
        long totalFlow = 0;

        for (int i = 0; i < solved.Count; i++)
        {
            var edge = solved[i];

            bool edgeLeavesSource = edge.From == sourceId;
            bool edgeHasPositiveFlow = edge.Flow > 0;

            if (edgeLeavesSource && edgeHasPositiveFlow)
            {
                totalFlow += edge.Flow;
            }
        }

        return totalFlow;
    }

    public MaxFlowResponse ComputeMaxFlowWithPath(
        CapacityGraph capacityGraph,
        FlowRequest request,
        UInt256 targetFlow)
    {
        /* --------------------------------------------------------------------
         * 1. Resolve ids and basic guards
         * ------------------------------------------------------------------ */
        int sinkId = AddressIdPool.IdOf(request.Sink);
        int effSink = capacityGraph.VirtualSinkAddress ?? sinkId;
        int sourceId = AddressIdPool.IdOf(request.Source);

        bool srcMissing = !capacityGraph.AvatarNodes.ContainsKey(sourceId);
        bool dstMissing = !capacityGraph.AvatarNodes.ContainsKey(effSink);
        if (srcMissing)
            throw new ArgumentException($"Source '{request.Source}' isn't in the graph snapshot.");
        if (dstMissing)
            throw new ArgumentException($"Sink '{request.Sink}' isn't in the graph snapshot.");

        /* --------------------------------------------------------------------
         * 2. Run max-flow and peel paths
         * ------------------------------------------------------------------ */
        long tgt = CirclesConverter.TruncateToInt64(targetFlow);
        var solved = MaxFlowSolver.Solve(capacityGraph.Edges, sourceId, effSink, tgt);
        var simplePaths = PathUtils.ExtractFlowPaths(solved, sourceId, effSink);

        /* --------------------------------------------------------------------
         * 2a. Optional quantization for invitation module (96 CRC chunks)
         *     Only sink-bound transfers are quantized; intermediates pass through.
         *     Number of invites is derived from targetFlow: invites = targetFlow / 96 CRC.
         * ------------------------------------------------------------------ */
        if (request.QuantizedMode == true)
        {
            // 96 CRC in 6-decimal precision = 96 * 10^6
            const long InvitationQuanta = 96_000_000L;

            simplePaths = PathUtils.QuantizeSinkBoundFlows(
                simplePaths,
                effSink,
                InvitationQuanta,
                tgt); // targetFlow determines how many invites to find
        }

        /* --------------------------------------------------------------------
         * 2b. Optional pruning to fit a transfer-step budget
         *      We keep the biggest-flow paths first and count "steps" after
         *      collapsing balance nodes (avatar→avatar per token).
         * ------------------------------------------------------------------ */
        bool hasStepCap = request.MaxTransfers.HasValue && request.MaxTransfers.Value > 0;
        if (hasStepCap)
        {
            int stepCap = request.MaxTransfers!.Value;
            int currentSteps = CountCollapsedTransferSteps(simplePaths, capacityGraph);

            bool needsPrune = currentSteps > stepCap;
            if (needsPrune)
            {
                simplePaths = PrunePathsByStepLimit(simplePaths, stepCap, capacityGraph);
            }
        }

        /* --------------------------------------------------------------------
         * 3. Convert SimpleEdge → FlowEdge
         * ------------------------------------------------------------------ */
        var flowPaths = new List<List<FlowEdge>>(simplePaths.Count);

        foreach (var path in simplePaths)
        {
            var list = new List<FlowEdge>(path.Count);
            foreach (var e in path)
            {
                var fe = new FlowEdge(e.From, e.To, e.Token, e.Capacity)
                {
                    Flow = e.Flow,
                    CurrentCapacity = e.Capacity
                };
                list.Add(fe);
            }

            flowPaths.Add(list);
        }

        /* --------------------------------------------------------------------
         * 4. Replace virtual-sink ids (if any) with the real sink id
         * ------------------------------------------------------------------ */
        if (capacityGraph.VirtualSinkAddress != null)
        {
            int vs = capacityGraph.VirtualSinkAddress.Value;

            var replaced = new List<List<FlowEdge>>(flowPaths.Count);
            foreach (var path in flowPaths)
            {
                var fixedPath = new List<FlowEdge>(path.Count);
                foreach (var fe in path)
                {
                    int from = fe.From == vs ? sinkId : fe.From;
                    int to = fe.To == vs ? sinkId : fe.To;

                    var copy = new FlowEdge(from, to, fe.Token, fe.InitialCapacity)
                    {
                        Flow = fe.Flow,
                        CurrentCapacity = fe.CurrentCapacity
                    };
                    fixedPath.Add(copy);
                }

                replaced.Add(fixedPath);
            }

            flowPaths = replaced;
        }

        /* --------------------------------------------------------------------
         * 5. Collapse balance nodes + aggregate identical edges
         * ------------------------------------------------------------------ */
        var collapsed = CollapseBalanceNodes(flowPaths, capacityGraph);
        var aggregated = collapsed.AggregateIdenticalEdges();

        /* --------------------------------------------------------------------
         * 6. Post-process to insert Router between Avatar → Group transfers
         * ------------------------------------------------------------------ */
        var processedEdges = InsertRouterInTransfers(aggregated.Edges, capacityGraph);

        /* --------------------------------------------------------------------
         * 6b. Sort edges to ensure mint dependencies are satisfied.
         *     All collateral edges (Router→Group) must precede the group's
         *     outbound mint edge (Group→Avatar) for contract execution to succeed.
         * ------------------------------------------------------------------ */
        var sortedEdges = SortEdgesForMintDependencies(processedEdges, capacityGraph);

        /* --------------------------------------------------------------------
         * 6c. Validate the edge ordering - throw if mint dependencies violated
         * ------------------------------------------------------------------ */
        ValidateMintEdgeOrdering(sortedEdges, capacityGraph);

        /* --------------------------------------------------------------------
         * 7. Build DTOs
         * ------------------------------------------------------------------ */
        var transfer = new List<TransferPathStep>();

        foreach (var e in sortedEdges)
        {
            if (e.Flow <= 0)
            {
                continue;
            }

            var step = new TransferPathStep
            {
                From = AddressIdPool.StringOf(e.From),
                To = AddressIdPool.StringOf(e.To),
                TokenOwner = AddressIdPool.StringOf(e.Token),
                Value = CirclesConverter
                    .BlowUpToUInt256(e.Flow)
                    .ToString(CultureInfo.InvariantCulture)
            };
            transfer.Add(step);
        }

        UInt256 maxFlowWei = 0;
        foreach (var t in transfer)
        {
            bool fromIsSource = AddressIdPool.IdOf(t.From) == sourceId;
            if (fromIsSource)
                maxFlowWei += UInt256.Parse(t.Value);
        }

        return new MaxFlowResponse(
            maxFlowWei.ToString(CultureInfo.InvariantCulture),
            transfer);
    }

    /* ------------------------------------------------------------------------
     * Post-process: Insert Router node between Avatar → Group transfers.
     * The router itself is not part of the capacity graph during pathfinding,
     * but the contract requires all Avatar→Group transfers to route through it.
     * This method splits any Avatar→Group edge into Avatar→Router→Group.
     * Group→Avatar minting transfers are left as-is (direct edge).
     * --------------------------------------------------------------------- */
    private List<FlowEdge> InsertRouterInTransfers(List<FlowEdge> transfers, CapacityGraph capacityGraph)
    {
        if (capacityGraph.RouterNode == null)
            return transfers;

        var result = new List<FlowEdge>();
        int routerId = capacityGraph.RouterNode.Value;

        foreach (var transfer in transfers)
        {
            // If this is Avatar → Group transfer, insert Router in between
            // (Group → Avatar minting transfers are left as-is)
            if (!capacityGraph.IsGroup(transfer.From) && 
                !capacityGraph.IsRouter(transfer.From) &&
                capacityGraph.IsGroup(transfer.To))
            {
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
     * --------------------------------------------------------------------- */
    private FlowGraph CollapseBalanceNodes(List<List<FlowEdge>> pathsWithFlow, CapacityGraph capacityGraph)
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

        /* ---------------- aggregate flows ----------------- */
        var agg = new Dictionary<(int From, int To, int Token), long>();

        foreach (var path in pathsWithFlow)
        {
            CollapseSinglePath(path, capacityGraph, agg);
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

        return collapsed;
    }

    private void CollapseSinglePath(
        List<FlowEdge> path,
        CapacityGraph capacityGraph,
        Dictionary<(int From, int To, int Token), long> agg)
    {
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
                    
                    // Collapse Avatar → TokenPool → (Avatar/Group)
                    // Result: Avatar → (Avatar/Group)
                    AddToAggregation(agg, e.From, next.To, e.Token, Math.Min(e.Flow, next.Flow));
                    i += 2;
                    continue;
                }
            }

            // Case 2: Group → Avatar (group token minting, keep as-is)
            if (capacityGraph.IsGroup(e.From))
            {
                // Keep Group → Avatar edge with group token
                AddToAggregation(agg, e.From, e.To, e.Token, e.Flow);
                i += 1;
                continue;
            }

            // Case 3: Any other direct edge (keep as-is)
            if (!IsPoolNode(e.To))
            {
                AddToAggregation(agg, e.From, e.To, e.Token, e.Flow);
                i += 1;
                continue;
            }

            // Shouldn't reach here in normal operation, but handle gracefully
            i += 1;
        }
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
            agg[key] = existing + flow;
        }
        else
        {
            agg[key] = flow;
        }
    }

    private bool IsBalanceNode(int addr) => AddressIdPool.IsBalanceNode(addr);
    
    private bool IsPoolNode(int addr)
    {
        if (!AddressIdPool.IsBalanceNode(addr)) return false;
        var str = AddressIdPool.StringOf(addr);
        return str.StartsWith("tpool-");
    }

    // Count how many (From,To,Token) transfer steps remain after collapsing
    private static int CountCollapsedTransferSteps(IReadOnlyList<List<SimpleEdge>> paths, CapacityGraph capacityGraph)
    {
        var unique = new HashSet<(int From, int To, int Token)>();

        for (int i = 0; i < paths.Count; i++)
        {
            var triples = CollapsePathToTransfers(paths[i], capacityGraph);
            for (int j = 0; j < triples.Count; j++)
            {
                unique.Add(triples[j]);
            }
        }

        return unique.Count;
    }

    // Greedy pruning: pick paths that give the highest flow per *marginal* step
    private static List<List<SimpleEdge>> PrunePathsByStepLimit(
        IReadOnlyList<List<SimpleEdge>> original,
        int stepCap,
        CapacityGraph capacityGraph)
    {
        // Precompute collapsed triples for each path + path flow.
        var metas = new List<(int Index, long Flow, HashSet<(int F, int T, int K)> Triples)>(original.Count);
        for (int i = 0; i < original.Count; i++)
        {
            var path = original[i];
            long flow = 0;
            if (path.Count > 0)
            {
                flow = path[0].Flow;
            }

            var triples = new HashSet<(int F, int T, int K)>(CollapsePathToTransfers(path, capacityGraph)
                .Select(t => (t.From, t.To, t.Token)));

            metas.Add((i, flow, triples));
        }

        var picked = new bool[original.Count];
        var selectedEdges = new HashSet<(int F, int T, int K)>();
        int stepsLeft = stepCap;

        while (stepsLeft > 0)
        {
            int bestIdx = -1;
            long bestFlow = 0;
            int bestDelta = 0;

            for (int i = 0; i < metas.Count; i++)
            {
                if (picked[i])
                {
                    continue;
                }

                // How many *new* steps would this path introduce?
                int delta = 0;
                foreach (var tr in metas[i].Triples)
                {
                    bool isNew = !selectedEdges.Contains(tr);
                    if (isNew)
                    {
                        delta++;
                    }
                }

                bool fitsBudget = delta <= stepsLeft;
                if (!fitsBudget)
                {
                    continue;
                }

                // Prefer zero-delta (free) additions first.
                if (bestIdx == -1)
                {
                    bestIdx = i;
                    bestFlow = metas[i].Flow;
                    bestDelta = delta;
                    continue;
                }

                if (delta == 0 && bestDelta != 0)
                {
                    bestIdx = i;
                    bestFlow = metas[i].Flow;
                    bestDelta = 0;
                    continue;
                }

                if (delta == 0 && bestDelta == 0)
                {
                    bool betterFlow = metas[i].Flow > bestFlow;
                    if (betterFlow)
                    {
                        bestIdx = i;
                        bestFlow = metas[i].Flow;
                        bestDelta = 0;
                    }

                    continue;
                }

                if (bestDelta == 0)
                {
                    // Current best is free; keep it.
                    continue;
                }

                // Compare flow/delta without floating point: a/b > c/d  <=>  a*d > c*b
                long a = metas[i].Flow;
                long b = delta;
                long c = bestFlow;
                long d = bestDelta;

                bool betterRatio = a * d > c * b;
                bool tieBreakFewerSteps = a * d == c * b && delta < bestDelta;
                bool better = betterRatio || tieBreakFewerSteps;

                if (better)
                {
                    bestIdx = i;
                    bestFlow = metas[i].Flow;
                    bestDelta = delta;
                }
            }

            if (bestIdx == -1)
            {
                // Nothing else fits in the remaining budget.
                break;
            }

            // Commit the chosen path
            picked[bestIdx] = true;
            foreach (var tr in metas[bestIdx].Triples)
            {
                bool added = selectedEdges.Add(tr);
                if (added)
                {
                    stepsLeft--;
                    if (stepsLeft == 0)
                    {
                        break;
                    }
                }
            }
        }

        // Preserve original order for stability.
        var pruned = new List<List<SimpleEdge>>();
        for (int i = 0; i < original.Count; i++)
        {
            if (picked[i])
            {
                pruned.Add(original[i]);
            }
        }

        return pruned;
    }

    // Collapse ONE peeled path into transfer triples (FromAvatar, ToAvatar, Token).
    private static List<(int From, int To, int Token)> CollapsePathToTransfers(
        List<SimpleEdge> path, 
        CapacityGraph capacityGraph)
    {
        var triples = new List<(int From, int To, int Token)>(Math.Max(1, path.Count));

        int i = 0;
        while (i < path.Count)
        {
            var e = path[i];

            // Check if this is a pool node
            bool eToIsPool = AddressIdPool.IsBalanceNode(e.To) && 
                            AddressIdPool.StringOf(e.To).StartsWith("tpool-");
            
            // Standard pool collapse: Avatar → TokenPool → Next
            if (eToIsPool)
            {
                bool hasNext = (i + 1) < path.Count;
                if (hasNext && path[i + 1].From == e.To)
                {
                    var next = path[i + 1];
                    // Collapse to Avatar → Next
                    triples.Add((e.From, next.To, e.Token));
                    i += 2;
                    continue;
                }
            }

            // Direct edges (including Group → Avatar minting)
            triples.Add((e.From, e.To, e.Token));
            i += 1;
        }

        return triples;
    }

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
    private List<FlowEdge> SortEdgesForMintDependencies(List<FlowEdge> edges, CapacityGraph capacityGraph)
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
    private void ValidateMintEdgeOrdering(List<FlowEdge> edges, CapacityGraph capacityGraph)
    {
        if (capacityGraph.RouterNode == null || capacityGraph.GroupNodes.Count == 0)
        {
            // No router or no groups - nothing to validate
            return;
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
                    throw new InvalidOperationException(
                        $"Edge ordering violation: Router → Group edge for group {groupAddr} " +
                        $"appears after Group → Avatar edge at index {i}. " +
                        "All collateral must be deposited before minting.");
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
                    throw new InvalidOperationException(
                        $"Flow violation: Group {groupAddr} has insufficient collateral at edge index {i}. " +
                        $"Cumulative inbound: {inbound}, cumulative outbound required: {groupOutboundFlow[groupId]}. " +
                        "Ensure all Router → Group edges precede Group → Avatar edges.");
                }
            }
        }
    }
}