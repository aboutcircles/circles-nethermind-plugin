using System.Numerics;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;

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
        : this(graphFactory)
    {
        _loadGraph = loadGraph;
    }

    public async Task<MaxFlowResponse> ComputeMaxFlow(FlowRequest request)
    {
        if (string.IsNullOrEmpty(request.Source) || string.IsNullOrEmpty(request.Sink))
        {
            throw new ArgumentException("Source and Sink must be provided.");
        }

        if (!BigInteger.TryParse(request.TargetFlow, out var targetFlow))
        {
            throw new ArgumentException("TargetFlow must be a valid integer.");
        }

        if (_loadGraph == null || _graphFactory == null)
        {
            throw new InvalidOperationException("LoadGraph and GraphFactory must be provided.");
        }

        // Load Trust and Balance Graphs
        var trustGraph = _graphFactory.V2TrustGraph(_loadGraph, request);
        var balanceGraph = _graphFactory.V2BalanceGraph(_loadGraph, request);

        return ComputeMaxFlowWithData(balanceGraph, trustGraph, request, targetFlow);
    }

    public MaxFlowResponse ComputeMaxFlowWithData(
        BalanceGraph balanceGraph,
        TrustGraph trustGraph,
        FlowRequest request,
        BigInteger targetFlow)
    {
        var source = request.Source?.ToLowerInvariant() ?? "";
        var sink = request.Sink?.ToLowerInvariant() ?? "";

        // Create Capacity Graph
        var capacityGraph = _graphFactory.CreateCapacityGraph(balanceGraph, trustGraph);

        // Create Flow Graph
        var flowGraph = _graphFactory.CreateFlowGraph(capacityGraph);

        // Validate Source and Sink
        if (!flowGraph.Nodes.ContainsKey(source))
        {
            throw new ArgumentException($"Source node '{request.Source}' does not exist in the graph.");
        }

        if (!flowGraph.Nodes.ContainsKey(sink))
        {
            throw new ArgumentException($"Sink node '{request.Sink}' does not exist in the graph.");
        }

        // Compute Max Flow
        var maxFlow = flowGraph.ComputeMaxFlowWithPaths(source, sink, targetFlow);

        // Extract Paths with Flow
        var pathsWithFlow = flowGraph.ExtractPathsWithFlow(source, sink, BigInteger.Parse("0"));

        // Collapse balance nodes to get a collapsed graph
        var collapsedGraph = CollapseBalanceNodes(pathsWithFlow);

        // Create transfer steps from the collapsed graph
        var transferSteps = new List<TransferPathStep>();

        foreach (var edge in collapsedGraph.Edges)
        {
            // For each edge, create a transfer step
            if (edge.Flow == BigInteger.Zero)
            {
                // Filter reverse edges
                continue;
            }

            transferSteps.Add(new TransferPathStep
            {
                From = edge.From,
                To = edge.To,
                TokenOwner = edge.Token,
                Value = edge.Flow.ToString()
            });
        }

        // Prepare the response
        var response = new MaxFlowResponse(maxFlow.ToString(), transferSteps);

        return response;
    }

    /// <summary>
    /// Collapses balance nodes in the paths and returns a collapsed flow graph.
    /// </summary>
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
                    var mergedFlow = BigInteger.Min(currentEdge.Flow, nextEdge.Flow);

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
                        Console.WriteLine(e.StackTrace);
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
                        collapsedGraph.AddFlowEdge(collapsedGraph, currentEdge);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        Console.WriteLine(e.StackTrace);
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
    private bool IsBalanceNode(string nodeAddress)
    {
        return nodeAddress.Contains("-");
    }
}