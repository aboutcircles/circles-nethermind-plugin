using System.Globalization;
using System.Text;
using Circles.Index.Utils;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Nethermind.Int256;

namespace Circles.Pathfinder;

public class V2Pathfinder : IPathfinder
{
    private readonly LoadGraph? _loadGraph;
    private readonly GraphFactory _graphFactory;

    public V2Pathfinder(GraphFactory graphFactory)
    {
        _graphFactory = graphFactory;
    }

    public V2Pathfinder(LoadGraph loadGraph, GraphFactory graphFactory)
    {
        _loadGraph = loadGraph;
        _graphFactory = graphFactory;
    }

    public Task<MaxFlowResponse> ComputeMaxFlow(FlowRequest request)
    {
        if (string.IsNullOrEmpty(request.Source) || string.IsNullOrEmpty(request.Sink))
        {
            throw new ArgumentException("Source and Sink must be provided.");
        }

        if (request.TargetFlow == null)
        {
            throw new ArgumentException("TargetFlow must be provided.");
        }

        if (!UInt256.TryParse(request.TargetFlow, out var targetFlow))
        {
            throw new ArgumentException("TargetFlow must be a valid integer.");
        }

        if (_loadGraph == null || _graphFactory == null)
        {
            throw new InvalidOperationException("LoadGraph and GraphFactory must be provided.");
        }

        //  // Console.WriteLine($"Requests Source: {request.Source?.ToLower()}");
        //  // Console.WriteLine($"Requests Sink: {request.Sink?.ToLower()} ");
        // Load Trust and Balance Graphs
        var trustGraph = _graphFactory.V2TrustGraph(_loadGraph);
        var balanceGraph = _graphFactory.V2BalanceGraph(_loadGraph);

        var maxFlowResponse = ComputeMaxFlowWithData(balanceGraph, trustGraph, request, targetFlow);

        return Task.FromResult(maxFlowResponse);
    }

    public MaxFlowResponse ComputeMaxFlowWithData(
        BalanceGraph balanceGraph,
        TrustGraph trustGraph,
        FlowRequest request,
        UInt256 targetFlow)
    {
        request.WithWrap ??= false;

        // Create capacity graph
        var capacityGraph = _graphFactory.CreateCapacityGraph(balanceGraph, trustGraph, request);

        // If we created a virtual sink inside capacityGraph, use that as the sink
        var sinkId = AddressIdPool.IdOf(request.Sink);
        int effectiveSink = sinkId;
        if (capacityGraph.VirtualSinkAddress != null)
        {
            effectiveSink = capacityGraph.VirtualSinkAddress.Value;
        }

        // Build flow graph
        var flowGraph = _graphFactory.CreateFlowGraph(capacityGraph);

        // Validate source + sink
        var sourceId = AddressIdPool.IdOf(request.Source);
        if (!flowGraph.Nodes.ContainsKey(sourceId))
        {
            throw new ArgumentException($"Source node '{request.Source}' does not exist in the graph.");
        }

        if (!flowGraph.Nodes.ContainsKey(effectiveSink))
        {
            throw new ArgumentException(
                $"Sink node '{(capacityGraph.VirtualSinkAddress != null ? "virtual sink" : request.Sink)}' does not exist in the graph.");
        }

        // Compute max flow
        var maxFlow = ConversionUtils.BlowUpToUInt256(
            flowGraph.ComputeMaxFlowWithPaths(
                sourceId,
                effectiveSink,
                ConversionUtils.TruncateToInt64(targetFlow)
            )
        );

        // // Console.WriteLine($"[Circles.V2Pathfinder] Max flow from {source} to {sink}: {maxFlow} (target: {targetFlow})");

        // Extract the paths
        var pathsWithFlow = flowGraph.ExtractPathsWithFlow(sourceId, effectiveSink, 0L);

        FlowLogger.LogTransferStepsFlow("[Circles.V2Pathfinder] pathsWithFlow before VirtualSink processing", sourceId,
            sinkId, pathsWithFlow);


        // If we had a virtual sink, rewrite it as the real sink in final edges 
        if (capacityGraph.VirtualSinkAddress != null)
        {
            pathsWithFlow = pathsWithFlow
                .Select(path =>
                    path.Select(edge => new FlowEdge(
                            edge.From == capacityGraph.VirtualSinkAddress
                                ? sinkId
                                : edge.From,
                            edge.To == capacityGraph.VirtualSinkAddress
                                ? sinkId
                                : edge.To,
                            edge.Token,
                            edge.InitialCapacity)
                        {
                            Flow = edge.Flow,
                            CurrentCapacity = edge.CurrentCapacity
                        })
                        .ToList()
                ).ToList();
        }

        FlowLogger.LogTransferStepsFlow(
            "[Circles.V2Pathfinder] pathsWithFlow after VirtualSink processing",
            sourceId,
            sinkId,
            pathsWithFlow);

        // Collapse balance nodes, etc. 
        var collapsedGraph = CollapseBalanceNodes(pathsWithFlow);

        FlowLogger.LogFlowGraphFlow("[Circles.V2Pathfinder] collapsedGraph", sourceId, sinkId, collapsedGraph);


        // Aggregate identical edges (from, to, token) 
        var aggregatedGraph = collapsedGraph.AggregateIdenticalEdges();

        FlowLogger.LogFlowGraphFlow("[Circles.V2Pathfinder] aggregatedGraph", sourceId, sinkId, aggregatedGraph);

        var transferSteps = new List<TransferPathStep>();
        foreach (var edge in aggregatedGraph.Edges)
        {
            if (edge.Flow == 0)
                continue;

            transferSteps.Add(new TransferPathStep
            {
                From = AddressIdPool.StringOf(edge.From),
                To = AddressIdPool.StringOf(edge.To),
                TokenOwner = AddressIdPool.StringOf(edge.Token),
                Value = ConversionUtils.BlowUpToUInt256(edge.Flow)
                    .ToString(CultureInfo.InvariantCulture)
            });
        }

        FlowLogger.LogTransferStepsFlow("[Circles.V2Pathfinder] transferSteps", sourceId, sinkId, transferSteps);

        //  var totalValue = transferSteps.Sum(o => Convert.ToInt64(o.Value));
        // Console.WriteLine($"-----------------------------------");
        // Console.WriteLine($"Target flow: {targetFlow}");
        // Console.WriteLine($"Max flow: {maxFlow}");
        //  // Console.WriteLine($"Total value: {totalValue}");

        // Return
        var response = new MaxFlowResponse(
            maxFlow.ToString(CultureInfo.InvariantCulture),
            transferSteps
        );
        return response;
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
            collapsed.AvatarNodes[from].OutEdges.Add(e);
            collapsed.AvatarNodes[to].InEdges.Add(e);
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

    static class FlowLogger
    {
        public static void LogPaths(string sink, List<List<FlowEdge>> transferPathSteps)
        {
            // Log the flows:
            StringBuilder sbb = new StringBuilder();
            sbb.AppendLine("Discovered paths:");
            sbb.AppendLine("----------------------------");
            foreach (var p in transferPathSteps)
            {
                sbb.AppendLine($"{p[0].From} -> {p[^1].To} ({p[^1].Flow}):");
                foreach (var flowEdge in p)
                {
                    sbb.Append(flowEdge.From);
                    sbb.Append("->");
                }

                sbb.AppendLine(sink);
                sbb.AppendLine();
            }

            // Console.WriteLine(sbb.ToString());
        }

        public static void LogFlowGraphFlow(string title, int source, int sink, FlowGraph flowGraph)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(title);
            sb.AppendLine("-----------------------");

            long flowFromSource = 0;
            foreach (var edge in flowGraph.Edges)
            {
                if (edge.From == source)
                {
                    flowFromSource += edge.Flow;
                }
            }

            long flowToSink = 0;
            foreach (var edge in flowGraph.Edges)
            {
                if (edge.To == sink)
                {
                    flowToSink += edge.Flow;
                }
            }

            sb.AppendLine($"  Flow from {AddressIdPool.StringOf(source)} to {AddressIdPool.StringOf(sink)}");
            sb.AppendLine($"  Flow from source: {flowFromSource}");
            sb.AppendLine($"  Flow to sink: {flowToSink}");

            // Console.WriteLine(sb.ToString());
        }

        public static void LogTransferStepsFlow(string title, int source, int sink,
            List<List<FlowEdge>> transferPathSteps)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(title);
            sb.AppendLine("-----------------------");

            long flowFromSource = 0;
            long flowToSink = 0;

            foreach (var flow in transferPathSteps)
            {
                foreach (var transferPathStep in flow)
                {
                    if (transferPathStep.From == source)
                    {
                        flowFromSource += transferPathStep.Flow;
                    }
                }

                foreach (var transferPathStep in flow)
                {
                    if (transferPathStep.To == sink)
                    {
                        flowToSink += transferPathStep.Flow;
                    }
                }
            }

            sb.AppendLine($"  Flow from {AddressIdPool.StringOf(source)} to {AddressIdPool.StringOf(sink)}");
            sb.AppendLine($"  Flow from source: {flowFromSource}");
            sb.AppendLine($"  Flow to sink: {flowToSink}");

            // Console.WriteLine(sb.ToString());
        }

        public static void LogTransferStepsFlow(string title, int source, int sink,
            List<TransferPathStep> transferPathSteps)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(title);
            sb.AppendLine("-----------------------");

            UInt256 flowFromSource = 0;
            foreach (var transferPathStep in transferPathSteps)
            {
                if (AddressIdPool.IdOf(transferPathStep.From) == source)
                {
                    flowFromSource += UInt256.Parse(transferPathStep.Value);
                }
            }

            UInt256 flowToSink = 0;
            foreach (var transferPathStep in transferPathSteps)
            {
                if (AddressIdPool.IdOf(transferPathStep.To) == sink)
                {
                    flowToSink += UInt256.Parse(transferPathStep.Value);
                }
            }

            sb.AppendLine($"  Flow from {source} to {sink}");
            sb.AppendLine($"  Flow from source: {flowFromSource}");
            sb.AppendLine($"  Flow to sink: {flowToSink}");

            // Console.WriteLine(sb.ToString());
        }
    }
}