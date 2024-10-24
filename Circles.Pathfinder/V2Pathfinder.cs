using System.Numerics;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder;

public class V2Pathfinder : IPathfinder
{
    private readonly LoadGraph _loadGraph;
    private readonly GraphFactory _graphFactory;

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

        if (!BigInteger.TryParse(request.TargetFlow, out var targetFlow))
        {
            throw new ArgumentException("TargetFlow must be a valid integer.");
        }

        // Load Trust and Balance Graphs
        var trustGraph = _graphFactory.V2TrustGraph(_loadGraph);
        var balanceGraph = _graphFactory.V2BalanceGraph(_loadGraph);

        // Create Capacity Graph
        var capacityGraph = _graphFactory.CreateCapacityGraph(balanceGraph, trustGraph);

        // Create Flow Graph
        var flowGraph = _graphFactory.CreateFlowGraph(capacityGraph);

        // Validate Source and Sink
        if (!flowGraph.Nodes.ContainsKey(request.Source))
        {
            throw new ArgumentException($"Source node '{request.Source}' does not exist in the graph.");
        }

        if (!flowGraph.Nodes.ContainsKey(request.Sink))
        {
            throw new ArgumentException($"Sink node '{request.Sink}' does not exist in the graph.");
        }

        if (IsBalanceNode(request.Source))
        {
            throw new ArgumentException("Source node cannot be a balance node.");
        }

        if (IsBalanceNode(request.Sink))
        {
            throw new ArgumentException("Sink node cannot be a balance node.");
        }

        // Compute Max Flow
        var maxFlow = flowGraph.ComputeMaxFlowWithPaths(request.Source, request.Sink, targetFlow);

        // Extract Paths with Flow
        // (Don't consider paths smaller than 0.1 Circles)
        var pathsWithFlow =
            flowGraph.ExtractPathsWithFlow(request.Source, request.Sink, BigInteger.Parse("100000000000000000"));

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
                    var mergedFlow = BigInteger.Min(currentEdge.Flow, nextEdge.Flow);

                    var mergedEdge = new FlowEdge(
                        currentEdge.From,
                        nextEdge.To,
                        nextEdge.Token,
                        currentEdge.CurrentCapacity // Adjust as needed
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