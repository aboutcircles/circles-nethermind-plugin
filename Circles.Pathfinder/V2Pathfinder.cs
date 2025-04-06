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

        Console.WriteLine($"Requests Source: {request.Source?.ToLower()}");
        Console.WriteLine($"Requests Sink: {request.Sink?.ToLower()} ");
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
        var source = request.Source?.ToLower() ?? "";
        var sink = request.Sink?.ToLower() ?? "";

        request.WithWrap ??= false;

        // Create capacity graph
        var capacityGraph = _graphFactory.CreateCapacityGraph(balanceGraph, trustGraph, request);

        // If we created a virtual sink inside capacityGraph, use that as the sink
        var effectiveSink = capacityGraph.VirtualSinkAddress ?? sink;

        // Build flow graph
        var flowGraph = _graphFactory.CreateFlowGraph(capacityGraph);

        // Validate source + sink
        if (!flowGraph.Nodes.ContainsKey(source))
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
                source,
                effectiveSink,
                ConversionUtils.TruncateToInt64(targetFlow)
            )
        );

        Console.WriteLine($"[Circles.V2Pathfinder] Max flow from {source} to {sink}: {maxFlow} (target: {targetFlow})");

        // Extract the paths
        var pathsWithFlow = flowGraph.ExtractPathsWithFlow(source, effectiveSink, 0L);

        FlowLogger.LogTransferStepsFlow("[Circles.V2Pathfinder] pathsWithFlow before VirtualSink processing", source,
            sink, pathsWithFlow);

        // If we had a virtual sink, rewrite it as the real sink in final edges 
        if (capacityGraph.VirtualSinkAddress != null)
        {
            pathsWithFlow = pathsWithFlow
                .Select(path =>
                    path.Select(edge => new FlowEdge(
                            edge.From == capacityGraph.VirtualSinkAddress ? sink : edge.From,
                            edge.To == capacityGraph.VirtualSinkAddress ? sink : edge.To,
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
            source,
            sink,
            pathsWithFlow);

        // Collapse balance nodes, etc. 
        var collapsedPathsWithFlow = CollapseBalanceNodes(pathsWithFlow);

        FlowLogger.LogTransferStepsFlow("[Circles.V2Pathfinder] collapsedGraph", source, sink, collapsedPathsWithFlow);

        // Find edges with the same from and to
        var sameFromAndTo = collapsedPathsWithFlow
            .SelectMany(o => o)
            .GroupBy(o => (o.From, o.To))
            .Where(o => o.Count() > 1)
            .ToList();
        
        Console.WriteLine($"[Circles.V2Pathfinder] Potential to reduce steps: {sameFromAndTo.Count}");

        var transferSteps = new List<TransferPathStep>();
        foreach (var path in collapsedPathsWithFlow)
        {
            foreach (var step in path)
            {
                transferSteps.Add(new TransferPathStep
                {
                    From = step.From,
                    To = step.To,
                    TokenOwner = step.Token,
                    Value = ConversionUtils.BlowUpToUInt256(step.Flow).ToString(CultureInfo.InvariantCulture)
                });
            }
        }

        // Aggregate identical edges (from, to, token) 
        // var aggregatedGraph = collapsedGraph.AggregateIdenticalEdges();
        //
        // FlowLogger.LogFlowGraphFlow("[Circles.V2Pathfinder] aggregatedGraph", source, sink, collapsedGraph);

        // Build TransferPathStep list
        // var transferSteps = new List<TransferPathStep>();
        // foreach (var edge in aggregatedGraph.Edges)
        // {
        //     if (edge.Flow == 0)
        //         continue;
        //
        //     transferSteps.Add(new TransferPathStep
        //     {
        //         From = edge.From,
        //         To = edge.To,
        //         TokenOwner = edge.Token,
        //         Value = ConversionUtils.BlowUpToUInt256(edge.Flow)
        //             .ToString(CultureInfo.InvariantCulture)
        //     });
        // }
        //
        // FlowLogger.LogTransferStepsFlow("[Circles.V2Pathfinder] transferSteps", source, sink, transferSteps);

        //  var totalValue = transferSteps.Sum(o => Convert.ToInt64(o.Value));
        Console.WriteLine($"-----------------------------------");
        Console.WriteLine($"Target flow: {targetFlow}");
        Console.WriteLine($"Max flow: {maxFlow}");
        //  Console.WriteLine($"Total value: {totalValue}");

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
    private List<List<FlowEdge>> CollapseBalanceNodes(List<List<FlowEdge>> pathsWithFlow)
    {
        var collapsedPaths = new List<List<FlowEdge>>();

        foreach (var path in pathsWithFlow)
        {
            var filteredPath = path.Where(o => IsBalanceNode(o.From)).ToList();

            // Now create new edges that consider just the avatar node IDs:
            List<FlowEdge> avatarOnlyPath = filteredPath.Aggregate(new List<FlowEdge>(), (p, c) =>
            {
                var splitId = c.From.Split('-');
                var balanceHolder = splitId[0];
                var token = splitId[1];

                p.Add(new FlowEdge(balanceHolder, c.To, token, c.InitialCapacity)
                {
                    Flow = c.Flow,
                    CurrentCapacity = c.CurrentCapacity,
                    ReverseEdge = c.ReverseEdge
                });

                return p;
            });

            collapsedPaths.Add(avatarOnlyPath);
        }

        return collapsedPaths;
    }

    /// <summary>
    /// Determines if a given node address is a balance node.
    /// </summary>
    /// <param name="nodeAddress">The node address to check.</param>
    /// <returns>True if it's a balance node; otherwise, false.</returns>
    private bool IsBalanceNode(string nodeAddress)
    {
        return nodeAddress.Contains('-');
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

            Console.WriteLine(sbb.ToString());
        }

        public static void LogFlowGraphFlow(string title, string source, string sink, FlowGraph flowGraph)
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

            sb.AppendLine($"  Flow from {source} to {sink}");
            sb.AppendLine($"  Flow from source: {flowFromSource}");
            sb.AppendLine($"  Flow to sink: {flowToSink}");

            Console.WriteLine(sb.ToString());
        }

        public static void LogTransferStepsFlow(string title, string source, string sink,
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

            sb.AppendLine($"  Flow from {source} to {sink}");
            sb.AppendLine($"  Flow from source: {flowFromSource}");
            sb.AppendLine($"  Flow to sink: {flowToSink}");

            Console.WriteLine(sb.ToString());
        }

        public static void LogTransferStepsFlow(string title, string source, string sink,
            List<TransferPathStep> transferPathSteps)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(title);
            sb.AppendLine("-----------------------");

            UInt256 flowFromSource = 0;
            foreach (var transferPathStep in transferPathSteps)
            {
                if (transferPathStep.From == source)
                {
                    flowFromSource += UInt256.Parse(transferPathStep.Value);
                }
            }

            UInt256 flowToSink = 0;
            foreach (var transferPathStep in transferPathSteps)
            {
                if (transferPathStep.To == sink)
                {
                    flowToSink += UInt256.Parse(transferPathStep.Value);
                }
            }

            sb.AppendLine($"  Flow from {source} to {sink}");
            sb.AppendLine($"  Flow from source: {flowFromSource}");
            sb.AppendLine($"  Flow to sink: {flowToSink}");

            Console.WriteLine(sb.ToString());
        }
    }
}