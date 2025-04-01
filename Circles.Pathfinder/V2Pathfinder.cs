using System.Globalization;
using System.Numerics;
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

    public async Task<MaxFlowResponse> ComputeMaxFlow(FlowRequest request)
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


        return ComputeMaxFlowWithData(balanceGraph, trustGraph, request, targetFlow);
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
        var maxFlow =
            flowGraph.ComputeMaxFlowWithPaths(source, effectiveSink, ConversionUtils.TruncateToInt64(targetFlow));

        // Extract the paths
        var pathsWithFlow = flowGraph.ExtractPathsWithFlow(source, effectiveSink, 0L);

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

        // Collapse balance nodes, etc. 
        var collapsedGraph = CollapseBalanceNodes(pathsWithFlow);

        // Aggregate identical edges (from, to, token) 
        var aggregatedGraph = collapsedGraph.AggregateIdenticalEdges();

        // Build TransferPathStep list
        var transferSteps = new List<TransferPathStep>();
        foreach (var edge in aggregatedGraph.Edges)
        {
            if (edge.Flow == 0)
                continue;

            transferSteps.Add(new TransferPathStep
            {
                From = edge.From,
                To = edge.To,
                TokenOwner = edge.Token,
                Value = edge.Flow
                    .ToString(CultureInfo.InvariantCulture)
            });
        }

        var totalValue = transferSteps.Sum(o => Convert.ToInt64(o.Value));
        Console.WriteLine($"-----------------------------------");
        Console.WriteLine($"Target flow: {targetFlow}");
        Console.WriteLine($"Max flow: {maxFlow}");
        Console.WriteLine($"Total value: {totalValue}");

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
    private FlowGraph CollapseBalanceNodes(List<List<FlowEdge>> pathsWithFlow)
    {
        var collapsedGraph = new FlowGraph();

        // 1. Collect all avatar nodes
        var avatars = new HashSet<string>();
        pathsWithFlow.ForEach(o => o.ForEach(p =>
        {
            if (!IsBalanceNode(p.From))
            {
                avatars.Add(p.From);
            }

            if (!IsBalanceNode(p.To))
            {
                avatars.Add(p.To);
            }
        }));
        foreach (var avatar in avatars)
        {
            collapsedGraph.AddAvatar(avatar);
        }

        // 2. Remove all balance nodes, fuse the ends together, and add that edge to the new flow graph
        pathsWithFlow.ForEach(o =>
        {
            for (int i = 0; i < o.Count; i++)
            {
                var currentEdge = o[i];
                var nextEdge = i < o.Count - 1 ? o[i + 1] : null;

                if (IsBalanceNode(currentEdge.To) && nextEdge != null && nextEdge.From == currentEdge.To)
                {
                    // We are at a balance node, so we need to collapse it by merging currentEdge and nextEdge

                    // The flow through the balance node is limited by both the incoming and outgoing flows
                    var mergedFlow = Math.Min(currentEdge.Flow, nextEdge.Flow);

                    var mergedEdge = new FlowEdge(
                        currentEdge.From,
                        nextEdge.To,
                        nextEdge.Token,
                        currentEdge.CurrentCapacity
                    )
                    {
                        Flow = mergedFlow,
                        ReverseEdge = nextEdge.ReverseEdge
                    };
                    try
                    {
                        collapsedGraph.AddFlowEdge(collapsedGraph, mergedEdge);
                        i++; // Skip the nextEdge since we have merged it
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);

                        // Log the stack trace
                        Console.WriteLine(e.StackTrace);

                        // Unpack the inner exception(s) recursively
                        while (e.InnerException != null)
                        {
                            e = e.InnerException;
                            Console.WriteLine(e.Message);
                            Console.WriteLine(e.StackTrace);
                        }
                    }
                }
                else
                {
                    try
                    {
                        // If not a balance node, add the current edge to the collapsed graph
                        collapsedGraph.AddFlowEdge(collapsedGraph, currentEdge);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);

                        // Log the stack trace
                        Console.WriteLine(e.StackTrace);

                        // Unpack the inner exception(s) recursively
                        while (e.InnerException != null)
                        {
                            e = e.InnerException;
                            Console.WriteLine(e.Message);
                            Console.WriteLine(e.StackTrace);
                        }
                    }
                }
            }
        });
        return collapsedGraph;
    }

    /// <summary>
    /// Determines if a given node address is a balance node.
    /// </summary>
    /// <param name="nodeAddress">The node address to check.</param>
    /// <returns>True if it's a balance node; otherwise, false.</returns>
    private bool IsBalanceNode(string nodeAddress)
    {
        return nodeAddress.Contains("-");
    }
}