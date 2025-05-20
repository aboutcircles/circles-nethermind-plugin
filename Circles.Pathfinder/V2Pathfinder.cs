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
        var simplePath = PathUtils.ExtractFlowPaths(solved, sourceId, effSink);

        /* --------------------------------------------------------------------
         * 3. Convert SimpleEdge ➜ FlowEdge
         * ------------------------------------------------------------------ */
        var flowPaths = new List<List<FlowEdge>>(simplePath.Count);

        foreach (var path in simplePath)
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

                int toAvatar = toIsBal
                    ? int.Parse(AddressIdPool.StringOf(edge.To).Split('-')[0])
                    : edge.To;

                /* Balance-node chain “balance → avatar” collapses into one step */
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

    private bool IsBalanceNode(int addr) => AddressIdPool.IsBalanceNode(addr);
}