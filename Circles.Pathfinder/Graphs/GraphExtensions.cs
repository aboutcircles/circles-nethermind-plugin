using System.Diagnostics;
using Google.OrTools.Graph;
using Circles.Pathfinder.Edges;

namespace Circles.Pathfinder.Graphs;

public static class GraphExtensions
{
    /// <summary>
    /// Computes max flow via a super source. If the network can push targetFlow, 
    /// the flow will match it exactly. Otherwise, it's lower.
    /// </summary>
    public static long ComputeMaxFlowWithPaths(
        this FlowGraph graph,
        string source,
        string sink,
        long targetFlow)
    {
        Stopwatch sw = new();
        sw.Start();

        // 3) Map nodes
        var nodeIndices = new Dictionary<string, int>();
        int nodeIndex = 0;
        foreach (var node in graph.Nodes.Values)
        {
            nodeIndices[node.Address] = nodeIndex++;
        }

        int superSourceIndex = nodeIndex++;

        // 4) Create solver
        var maxFlowSolver = new MaxFlow();

        // 5) Scale targetFlow for superSource->realSource

        if (!nodeIndices.ContainsKey(source) || !nodeIndices.ContainsKey(sink))
        {
            throw new ArgumentException("Source or sink not in node map.");
        }

        int realSourceIndex = nodeIndices[source];
        int sinkIndex = nodeIndices[sink];

        // 6) superSource->realSource arc
        maxFlowSolver.AddArcWithCapacity(superSourceIndex, realSourceIndex, targetFlow);

        // 7) Add arcs for each edge
        var edgeToArc = new Dictionary<FlowEdge, int>();
        foreach (var edge in graph.Edges)
        {
            int fromIdx = nodeIndices[edge.From];
            int toIdx = nodeIndices[edge.To];

            edgeToArc[edge] = maxFlowSolver.AddArcWithCapacity(fromIdx, toIdx, edge.CurrentCapacity);
        }

        // 8) Solve from superSource->sink
        var status = maxFlowSolver.Solve(superSourceIndex, sinkIndex);
        if (status != MaxFlow.Status.OPTIMAL)
        {
            throw new Exception($"Max flow not optimal: {status}");
        }

        long scaledFlowVal = maxFlowSolver.OptimalFlow();

        // 9) Store each edge’s flow
        foreach (var edge in graph.Edges)
        {
            long arcFlow = maxFlowSolver.Flow(edgeToArc[edge]);

            edge.Flow += arcFlow; // accumulate flow
            edge.CurrentCapacity -= arcFlow;
            if (edge.ReverseEdge != null)
            {
                edge.ReverseEdge.CurrentCapacity += arcFlow;
            }
        }

        sw.Stop();
        Console.WriteLine($"TIMING: GraphExtensions.ComputeMaxFlowWithPaths: {sw.ElapsedMilliseconds}ms");

        return scaledFlowVal;
    }
}