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
            throw new ArgumentException($"Source '{flowRequest.Source}' isn’t in the graph snapshot.");
        }

        bool sinkMissing = !capacityGraph.AvatarNodes.ContainsKey(effSink);
        if (sinkMissing)
        {
            throw new ArgumentException($"Sink '{flowRequest.Sink}' isn’t in the graph snapshot.");
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
            throw new ArgumentException($"Source '{request.Source}' isn’t in the graph snapshot.");
        if (dstMissing)
            throw new ArgumentException($"Sink '{request.Sink}' isn’t in the graph snapshot.");

        /* --------------------------------------------------------------------
         * 2. Run max-flow and peel paths
         * ------------------------------------------------------------------ */
        long tgt = CirclesConverter.TruncateToInt64(targetFlow);
        var solved = MaxFlowSolver.Solve(capacityGraph.Edges, sourceId, effSink, tgt);
        var simplePaths = PathUtils.ExtractFlowPaths(solved, sourceId, effSink);

        /* --------------------------------------------------------------------
         * 2b. Optional pruning to fit a transfer-step budget
         *      We keep the biggest-flow paths first and count "steps" after
         *      collapsing balance nodes (avatar→avatar per token).
         * ------------------------------------------------------------------ */
        bool hasStepCap = request.MaxTransfers.HasValue && request.MaxTransfers.Value > 0;
        if (hasStepCap)
        {
            int stepCap = request.MaxTransfers!.Value;
            int currentSteps = CountCollapsedTransferSteps(simplePaths);

            bool needsPrune = currentSteps > stepCap;
            if (needsPrune)
            {
                simplePaths = PrunePathsByStepLimit(simplePaths, stepCap);
            }
        }

        /* --------------------------------------------------------------------
         * 3. Convert SimpleEdge ➜ FlowEdge
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
        var collapsed = CollapseBalanceNodes(flowPaths);
        var aggregated = collapsed.AggregateIdenticalEdges(); // legacy helper

        /* --------------------------------------------------------------------
         * 6. Build DTOs
         * ------------------------------------------------------------------ */
        var transfer = new List<TransferPathStep>();

        foreach (var e in aggregated.Edges)
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
 * Collapse balance nodes in a set of paths.
 * --------------------------------------------------------------------- */
    private FlowGraph CollapseBalanceNodes(List<List<FlowEdge>> pathsWithFlow)
    {
        var collapsed = new FlowGraph();

        /* ---------------- copy avatars that actually appear ---------------- */
        var avatarSet = new HashSet<int>();

        foreach (var path in pathsWithFlow)
        {
            foreach (var edge in path)
            {
                if (!IsBalanceNode(edge.From))
                    avatarSet.Add(edge.From);

                if (!IsBalanceNode(edge.To))
                    avatarSet.Add(edge.To);
            }
        }

        foreach (int a in avatarSet)
        {
            collapsed.AddAvatar(a);
        }

        /* ---------------- aggregate avatar-to-avatar flows ----------------- */
        var agg = new Dictionary<(int From, int To, int Token), long>();

        foreach (var path in pathsWithFlow)
        {
            for (int i = 0; i < path.Count; i++)
            {
                var e = path[i];

                bool eToIsPool = IsBalanceNode(e.To);
                bool hasNext = (i + 1) < path.Count;
                bool nextContinuesFromPool = hasNext && path[i + 1].From == e.To;
                bool isChain = eToIsPool && nextContinuesFromPool;

                if (isChain)
                {
                    // Fold Avatar → TokenPool(token) → Avatar
                    var next = path[i + 1];

                    int fromAvatar = e.From; // Avatar
                    int toAvatar = next.To; // Avatar
                    int token = e.Token;

                    long flow = Math.Min(e.Flow, next.Flow);
                    var key = (fromAvatar, toAvatar, token);

                    if (agg.TryGetValue(key, out long existing))
                    {
                        agg[key] = existing + flow;
                    }
                    else
                    {
                        agg[key] = flow;
                    }

                    i += 1; // consume the next edge too
                }
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

        return collapsed;
    }

    private bool IsBalanceNode(int addr) => AddressIdPool.IsBalanceNode(addr);

    // Count how many (From,To,Token) transfer steps remain after collapsing
    // all paths (Avatar→TokenPool→Avatar folds to Avatar→Avatar per token).
    private static int CountCollapsedTransferSteps(IReadOnlyList<List<SimpleEdge>> paths)
    {
        var unique = new HashSet<(int From, int To, int Token)>();

        for (int i = 0; i < paths.Count; i++)
        {
            var triples = CollapsePathToTransfers(paths[i]);
            for (int j = 0; j < triples.Count; j++)
            {
                unique.Add(triples[j]);
            }
        }

        return unique.Count;
    }

    // Greedy pruning: pick paths that give the highest flow per *marginal* step
    // (steps that aren't already "paid for" by previously picked paths), until
    // we reach the step budget.
    private static List<List<SimpleEdge>> PrunePathsByStepLimit(
        IReadOnlyList<List<SimpleEdge>> original,
        int stepCap)
    {
        // Precompute collapsed triples for each path + path flow.
        var metas = new List<(int Index, long Flow, HashSet<(int F, int T, int K)> Triples)>(original.Count);
        for (int i = 0; i < original.Count; i++)
        {
            var path = original[i];
            long flow = 0;
            if (path.Count > 0)
            {
                // Each edge in the peeled path has the same min-bottleneck flow.
                flow = path[0].Flow;
            }

            var triples = new HashSet<(int F, int T, int K)>(CollapsePathToTransfers(path)
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
    // Mirrors the logic in CollapseBalanceNodes, but works on SimpleEdge.
    private static List<(int From, int To, int Token)> CollapsePathToTransfers(List<SimpleEdge> path)
    {
        // Returns triples (AvatarFrom, AvatarTo, Token) for a single peeled path.
        // With pooled graphs, paths alternate Avatar→TokenPool→Avatar; we fold those pairs.
        var triples = new List<(int From, int To, int Token)>(Math.Max(1, path.Count / 2));

        int i = 0;
        while (i < path.Count)
        {
            var e = path[i];

            bool eToIsPool = AddressIdPool.IsBalanceNode(e.To);
            bool hasNext = (i + 1) < path.Count;
            bool nextContinuesFromPool = hasNext && path[i + 1].From == e.To;
            bool isChain = eToIsPool && nextContinuesFromPool;

            if (isChain)
            {
                // Fold Avatar → TokenPool(token) → Avatar
                var next = path[i + 1];
                triples.Add((e.From, next.To, e.Token));
                i += 2;
                continue;
            }

            // Fallback: treat as direct Avatar→Avatar hop (rare / not expected here)
            triples.Add((e.From, e.To, e.Token));
            i += 1;
        }

        return triples;
    }
}