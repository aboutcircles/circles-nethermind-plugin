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
         * 1) Guards
         * ------------------------------------------------------------------ */
        int sinkId = AddressIdPool.IdOf(request.Sink);
        int effSink = capacityGraph.VirtualSinkAddress ?? sinkId;
        int sourceId = AddressIdPool.IdOf(request.Source);

        bool srcMissing = !capacityGraph.AvatarNodes.ContainsKey(sourceId);
        bool dstMissing = !capacityGraph.AvatarNodes.ContainsKey(effSink);
        if (srcMissing)
        {
            throw new ArgumentException($"Source '{request.Source}' isn’t in the graph snapshot.");
        }

        if (dstMissing)
        {
            throw new ArgumentException($"Sink '{request.Sink}' isn’t in the graph snapshot.");
        }

        /* --------------------------------------------------------------------
         * 2) Solve + peel paths
         * ------------------------------------------------------------------ */
        long tgt = CirclesConverter.TruncateToInt64(targetFlow);
        var solved = MaxFlowSolver.Solve(capacityGraph.Edges, sourceId, effSink, tgt);
        var simplePaths = PathUtils.ExtractFlowPaths(solved, sourceId, effSink);

        /* --------------------------------------------------------------------
         * 3) Optional pruning to block gas budget
         * ------------------------------------------------------------------ */
        bool defaultPruneEnabled = GetDefaultPruneEnabled();
        bool shouldPrune = request.Prune ?? defaultPruneEnabled;

        if (shouldPrune && simplePaths.Count > 0)
        {
            long gasBudget = GetBlockGasLimit();
            int gasPerHop = GetGasPerHop();
            int gasPerPathOverhead = GetGasPerPathOverhead();

            simplePaths = PruneToGasBudget(simplePaths, gasBudget, gasPerHop, gasPerPathOverhead);
        }

        /* --------------------------------------------------------------------
         * 4) Convert SimpleEdge → FlowEdge
         * ------------------------------------------------------------------ */
        var flowPaths = new List<List<FlowEdge>>(simplePaths.Count);
        for (int i = 0; i < simplePaths.Count; i++)
        {
            var path = simplePaths[i];
            var list = new List<FlowEdge>(path.Count);

            for (int j = 0; j < path.Count; j++)
            {
                var e = path[j];
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
         * 5) Replace virtual-sink ids with real sink id
         * ------------------------------------------------------------------ */
        if (capacityGraph.VirtualSinkAddress != null)
        {
            int vs = capacityGraph.VirtualSinkAddress.Value;

            var replaced = new List<List<FlowEdge>>(flowPaths.Count);
            for (int i = 0; i < flowPaths.Count; i++)
            {
                var path = flowPaths[i];
                var fixedPath = new List<FlowEdge>(path.Count);

                for (int j = 0; j < path.Count; j++)
                {
                    var fe = path[j];
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
         * 6) Collapse balance nodes + aggregate identical edges
         * ------------------------------------------------------------------ */
        var collapsed = CollapseBalanceNodes(flowPaths);
        var aggregated = collapsed.AggregateIdenticalEdges();

        /* --------------------------------------------------------------------
         * 7) DTO
         * ------------------------------------------------------------------ */
        var transfer = new List<TransferPathStep>();
        for (int i = 0; i < aggregated.Edges.Count; i++)
        {
            var e = aggregated.Edges[i];
            bool hasFlow = e.Flow > 0;
            if (!hasFlow)
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
        for (int i = 0; i < transfer.Count; i++)
        {
            var t = transfer[i];
            bool fromIsSource = AddressIdPool.IdOf(t.From) == sourceId;
            if (fromIsSource)
            {
                maxFlowWei += UInt256.Parse(t.Value);
            }
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
                var edge = path[i];
                bool fromIsBal = IsBalanceNode(edge.From);
                bool toIsBal = IsBalanceNode(edge.To);

                /* Resolve avatar / token ids */
                int fromAvatar;
                int token;
                if (fromIsBal)
                {
                    string[] parts = AddressIdPool.StringOf(edge.From).Split('-');
                    fromAvatar = int.Parse(parts[0]);
                    token = int.Parse(parts[1]);
                }
                else
                {
                    fromAvatar = edge.From;
                    token = edge.Token;
                }

                /* Detect chain BEFORE computing toAvatar to avoid parsing "tpool-*" ids */
                bool beginsChain = toIsBal &&
                                   i + 1 < path.Count &&
                                   path[i + 1].From == edge.To;

                if (beginsChain)
                {
                    var nextEdge = path[++i]; // consume the next edge
                    int nextToAv = IsBalanceNode(nextEdge.To)
                        ? int.Parse(AddressIdPool.StringOf(nextEdge.To).Split('-')[0])
                        : nextEdge.To;

                    long flow = Math.Min(edge.Flow, nextEdge.Flow);
                    var key = (fromAvatar, nextToAv, token);

                    if (agg.TryGetValue(key, out long existing))
                        agg[key] = existing + flow;
                    else
                        agg[key] = flow;
                }
                else
                {
                    // Only compute toAvatar here (non-chain case)
                    int toAvatar = toIsBal
                        ? int.Parse(AddressIdPool.StringOf(edge.To).Split('-')[0])
                        : edge.To;

                    var key = (fromAvatar, toAvatar, token);
                    if (agg.TryGetValue(key, out long existing))
                        agg[key] = existing + edge.Flow;
                    else
                        agg[key] = edge.Flow;
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

    /* ------------------------------------------------------------------------
     * GAS-BUDGET PRUNING
     * --------------------------------------------------------------------- */
    private static List<List<SimpleEdge>> PruneToGasBudget(
        List<List<SimpleEdge>> paths,
        long gasBudget,
        int gasPerHop,
        int gasPerPathOverhead)
    {
        if (paths.Count == 0)
        {
            return paths;
        }

        // Build table: each path has (flow, hops, cost, path)
        var items = new List<(List<SimpleEdge> Path, long Flow, int Hops, long Cost)>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            var p = paths[i];
            int hops = p.Count;
            long flow = p[0].Flow; // invariant: all edges carry same flow after peeling
            long cost = gasPerPathOverhead + (long)hops * gasPerHop;
            items.Add((p, flow, hops, cost));
        }

        // Drop any path that by itself exceeds the gas budget (can’t be executed anyway).
        var feasible = new List<(List<SimpleEdge> Path, long Flow, int Hops, long Cost)>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            bool pathFits = items[i].Cost <= gasBudget;
            if (pathFits)
            {
                feasible.Add(items[i]);
            }
        }

        if (feasible.Count == 0)
        {
            // Nothing can be executed in a single block; return empty set.
            return new List<List<SimpleEdge>>(0);
        }

        long totalCost = 0;
        for (int i = 0; i < feasible.Count; i++)
        {
            totalCost += feasible[i].Cost;
        }

        bool underBudget = totalCost <= gasBudget;
        if (underBudget)
        {
            var ok = new List<List<SimpleEdge>>(feasible.Count);
            for (int i = 0; i < feasible.Count; i++)
            {
                ok.Add(feasible[i].Path);
            }

            return ok;
        }

        // Remove the smallest-flow paths first; on tie, remove the gas-heavier one.
        feasible.Sort((a, b) =>
        {
            int byFlow = a.Flow.CompareTo(b.Flow); // ascending flow
            if (byFlow != 0) return byFlow;
            return b.Cost.CompareTo(a.Cost); // heavier gas first on tie
        });

        int removeIdx = 0;
        while (totalCost > gasBudget && removeIdx < feasible.Count)
        {
            totalCost -= feasible[removeIdx].Cost;
            removeIdx++;
        }

        // Keep the suffix [removeIdx..end)
        var kept = new List<List<SimpleEdge>>(Math.Max(0, feasible.Count - removeIdx));
        for (int i = removeIdx; i < feasible.Count; i++)
        {
            kept.Add(feasible[i].Path);
        }

        return kept;
    }

    private static bool GetDefaultPruneEnabled()
    {
        string? raw = Environment.GetEnvironmentVariable("PATHFINDER_PRUNE_DEFAULT");
        return bool.TryParse(raw, out bool v) ? v : true;
    }

    private static long GetBlockGasLimit()
    {
        return 17_000_000L;
    }

    private static int GetGasPerHop()
    {
        // TODO:
        //   Gas per hop is variable and consists of:
        //   * unpackCoordinates
        //   * verifyFlowMatrix
        //     * isPermittedFlow
        //   * effectPathTransfers
        //     * update
        //       * balanceOfOnDay
        //       * TransferSingle
        //       * DiscountCost (not always)
        //       * updateBalance
        //       * discountAndAddToBalance
        //   * callAcceptanceChecks

        return 20_000;
    }

    private static int GetGasPerPathOverhead()
    {
        return 25_000;
    }

    private bool IsBalanceNode(int addr) => AddressIdPool.IsBalanceNode(addr);
}