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
        int source,
        int sink,
        long targetFlow)
    {
        Stopwatch sw = new();
        sw.Start();

        // 3) Map nodes
        var nodeIndices = new Dictionary<int, int>();
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

        // Check if any flow is actually going from source to other nodes or directly to sink
        bool hasRealFlow = false;
        for (int i = 0; i < maxFlowSolver.NumArcs(); ++i)
        {
            int tail = maxFlowSolver.Tail(i);
            long flow = maxFlowSolver.Flow(i);

            // Skip the superSource->source arc
            if (tail == superSourceIndex)
                continue;

            // If any other arc has flow, then we have a real path
            if (flow > 0)
            {
                hasRealFlow = true;
                break;
            }
        }

        // If there's no real flow in the network, return 0
        if (!hasRealFlow)
        {
            // Console.WriteLine("No actual flow paths found in the network. Returning max flow = 0.");
            return 0;
        }

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
        // Console.WriteLine($"TIMING: GraphExtensions.ComputeMaxFlowWithPaths: {sw.ElapsedMilliseconds}ms");

        return scaledFlowVal;
    }
}