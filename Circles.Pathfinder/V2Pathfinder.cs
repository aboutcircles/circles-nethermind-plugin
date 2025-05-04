using System.Globalization;
using Circles.Index.Utils;
using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Nethermind.Int256;

namespace Circles.Pathfinder;

public class V2Pathfinder
{
    public MaxFlowResponse ComputeMaxFlowOnCapacityGraph(
        CapacityGraph capacityGraph,
        FlowRequest request,
        UInt256 targetFlow)
    {
        // pick real vs virtual sink exactly like before
        int sinkId = AddressIdPool.IdOf(request.Sink);
        int effSink = capacityGraph.VirtualSinkAddress ?? sinkId;
        int sourceId = AddressIdPool.IdOf(request.Source);

        if (!capacityGraph.AvatarNodes.ContainsKey(sourceId))
            throw new ArgumentException($"Source '{request.Source}' isn’t in the graph snapshot.");
        if (!capacityGraph.AvatarNodes.ContainsKey(effSink))
            throw new ArgumentException($"Sink '{request.Sink}' isn’t in the graph snapshot.");
        
        // ------------------ max-flow -------------------------------------
        var edges = capacityGraph.Edges;
        long tgt = ConversionUtils.TruncateToInt64(targetFlow);

        var solved = MaxFlowSolver.Solve(edges, sourceId, effSink, tgt);
        var paths = PathUtils.ExtractFlowPaths(solved, sourceId, effSink);

        /* Re-use existing collapse / aggregate code by briefly mapping
           SimpleEdge ➜ FlowEdge (cheap objects, negligible GC). */
        var flowPaths = paths.Select(p => p.Select(e => new FlowEdge(
                    e.From, e.To, e.Token, e.Capacity)
                { Flow = e.Flow, CurrentCapacity = e.Capacity })
            .ToList()).ToList();

        if (capacityGraph.VirtualSinkAddress != null)
        {
            flowPaths = flowPaths.Select(path =>
                    path.Select(f => new FlowEdge(
                                f.From == capacityGraph.VirtualSinkAddress ? sinkId : f.From,
                                f.To == capacityGraph.VirtualSinkAddress ? sinkId : f.To,
                                f.Token,
                                f.InitialCapacity)
                            { Flow = f.Flow, CurrentCapacity = f.CurrentCapacity })
                        .ToList())
                .ToList();
        }

        var collapsed = CollapseBalanceNodes(flowPaths);
        var aggregated = collapsed.AggregateIdenticalEdges();

        // ------------------ DTO build ------------------------------------
        var transfer = aggregated.Edges
            .Where(e => e.Flow > 0)
            .Select(e => new TransferPathStep
            {
                From = AddressIdPool.StringOf(e.From),
                To = AddressIdPool.StringOf(e.To),
                TokenOwner = AddressIdPool.StringOf(e.Token),
                Value = ConversionUtils.BlowUpToUInt256(e.Flow).ToString(CultureInfo.InvariantCulture)
            })
            .ToList();

        var maxFlowWei = solved
            .Where(e => e.From == sourceId)
            .Aggregate((UInt256)0, (p, c) => p + ConversionUtils.BlowUpToUInt256(c.Flow));

        return new MaxFlowResponse(
            maxFlowWei.ToString(CultureInfo.InvariantCulture),
            transfer);
    }


    /// <summary>
    /// Collapses balance nodes in the paths and returns a collapsed flow graph.
    /// </summary>
    /// <param name="pathsWithFlow">The list of paths with flow.</param>
    /// <returns>A FlowGraph with balance nodes collapsed.</returns>
    /// <summary>
    /// </summary>
    private FlowGraph CollapseBalanceNodes(List<List<FlowEdge>> pathsWithFlow)
    {
        var collapsed = new FlowGraph();

        // copy every avatar that occurs in the paths
        var avatars = new HashSet<int>();
        foreach (var edge in pathsWithFlow.SelectMany(p => p))
        {
            if (!IsBalanceNode(edge.From))
            {
                avatars.Add(edge.From);
            }

            if (!IsBalanceNode(edge.To))
            {
                avatars.Add(edge.To);
            }
        }

        foreach (var a in avatars)
        {
            collapsed.AddAvatar(a);
        }

        // aggregate “avatar → avatar” flows, collapsing balance-chains
        var agg = new Dictionary<(int From, int To, int Token), long>();

        foreach (var path in pathsWithFlow)
        {
            for (int i = 0; i < path.Count; i++)
            {
                var edge = path[i];
                bool fromIsBal = IsBalanceNode(edge.From);
                bool toIsBal = IsBalanceNode(edge.To);

                int fromAvatar = fromIsBal
                    ? int.Parse(AddressIdPool.StringOf(edge.From).Split('-')[0])
                    : edge.From;
                int token = fromIsBal
                    ? int.Parse(AddressIdPool.StringOf(edge.From).Split('-')[1])
                    : edge.Token;
                int toAvatar = toIsBal
                    ? int.Parse(AddressIdPool.StringOf(edge.To).Split('-')[0])
                    : edge.To;

                bool beginsChain = toIsBal && i + 1 < path.Count && path[i + 1].From == edge.To;
                if (beginsChain)
                {
                    var nextEdge = path[++i];
                    int nextToAv = IsBalanceNode(nextEdge.To)
                        ? int.Parse(AddressIdPool.StringOf(nextEdge.To).Split('-')[0])
                        : nextEdge.To;

                    long flow = Math.Min(edge.Flow, nextEdge.Flow);
                    var key = (fromAvatar, nextToAv, token);
                    agg[key] = agg.TryGetValue(key, out var ex) ? ex + flow : flow;
                }
                else
                {
                    var key = (fromAvatar, toAvatar, token);
                    agg[key] = agg.TryGetValue(key, out var ex) ? ex + edge.Flow : edge.Flow;
                }
            }
        }

        // build collapsed edges
        foreach (var ((from, to, token), flow) in agg)
        {
            if (flow <= 0)
            {
                continue;
            }

            var e = new FlowEdge(from, to, token, long.MaxValue)
            {
                Flow = flow,
                CurrentCapacity = long.MaxValue - flow
            };

            collapsed.Edges.Add(e);
        }

        return collapsed;
    }

    /// <summary>
    /// Determines if a given node address is a balance node.
    /// </summary>
    /// <param name="nodeAddress">The node address to check.</param>
    /// <returns>True if it's a balance node; otherwise, false.</returns>
    private bool IsBalanceNode(int nodeAddress)
    {
        return AddressIdPool.IsBalanceNode(nodeAddress);
    }
}