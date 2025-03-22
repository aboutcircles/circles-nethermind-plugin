using System.Numerics;
using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Nethermind.Int256;

namespace Circles.Pathfinder;

public class V2Pathfinder(TrustGraph trustGraph, CapacityGraph capacityGraph, GraphFactory graphFactory)
{
    public async Task<MaxFlowResponse> ComputeMaxFlow(FlowRequest request)
    {
        var source = request.Source?.ToLowerInvariant() ?? throw new ArgumentException("Source must be provided.");
        var sink = request.Sink?.ToLowerInvariant() ?? throw new ArgumentException("Sink must be provided.");
        var targetFlowStr = request.TargetFlow ?? throw new ArgumentException("TargetFlow must be provided.");

        if (!UInt256.TryParse(targetFlowStr, out var targetFlow))
        {
            throw new ArgumentException("TargetFlow must be a valid integer.");
        }

        // Build the flow graph
        var flowGraph = graphFactory.CreateFlowGraph(capacityGraph);

        // Check existence
        if (!trustGraph.Nodes.ContainsKey(source))
            throw new ArgumentException($"Source node '{source}' does not exist in the graph.");
        if (!trustGraph.Nodes.ContainsKey(sink))
            throw new ArgumentException($"Sink node '{sink}' does not exist in the graph.");

        // Run max flow
        var maxFlow = flowGraph.ComputeMaxFlowWithPaths(source, sink, targetFlow);

        // Now decompose the final flows into disjoint paths
        var allPaths = flowGraph.ExtractPathsWithFlow(source, sink, UInt256.Zero);

        // Convert each path into "collapsed" edges that skip balance nodes
        var transferSteps = new List<TransferPathStep>();
        foreach (var pathEdges in allPaths)
        {
            // Collapse any intermediate "balance nodes" in this single path
            var collapsedEdges = CollapseBalanceNodesInPath(pathEdges);

            // Turn each collapsed edge into a TransferPathStep
            foreach (var e in collapsedEdges)
            {
                transferSteps.Add(new TransferPathStep
                {
                    From = e.From,
                    To = e.To,
                    TokenOwner = e.Token,
                    Value = e.Flow.ToString()
                });
            }
        }

        // Build final response
        var response = new MaxFlowResponse(
            maxFlow.ToString(),
            transferSteps
        );

        return response;
    }

    /// <summary>
    /// Collapse consecutive edges that pass through a single balance node in a single path.
    /// For example, if path is [A -> bal-XYZ, bal-XYZ -> B], we merge them into [A -> B].
    /// The returned edge has the same Flow, and token = second edge's token (or however you prefer).
    /// </summary>
    private List<FlowEdge> CollapseBalanceNodesInPath(List<FlowEdge> pathEdges)
    {
        var result = new List<FlowEdge>();
        int i = 0;
        while (i < pathEdges.Count)
        {
            var current = pathEdges[i];
            // If the current edge’s "to" is a balance node, and it is immediately followed
            // by an edge from that same balance node to something else, we can merge them.
            if (i + 1 < pathEdges.Count)
            {
                var next = pathEdges[i + 1];
                bool canMerge = IsBalanceNode(current.To) && (current.To == next.From);
                if (canMerge)
                {
                    // Flow on this path is uniform, so they have the same "pathFlow." 
                    // We’ll just keep the same flow as either edge (they’re identical).
                    UInt256 mergedFlow = current.Flow;
                    string token = next.Token;

                    // Create a merged edge from current.From -> next.To
                    var mergedEdge = new FlowEdge(current.From, next.To, token, UInt256.Zero)
                    {
                        Flow = mergedFlow
                    };
                    result.Add(mergedEdge);

                    i += 2; // skip both edges
                    continue;
                }
            }

            // Otherwise, just keep current edge
            result.Add(current);
            i += 1;
        }

        return result;
    }

    private bool IsBalanceNode(string address)
    {
        // e.g. if your balance nodes have a dash, or do some other check
        return address.Contains("-");
    }
}