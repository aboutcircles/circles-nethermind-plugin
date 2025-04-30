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
    public static long ComputeMaxFlowWithPaths(this FlowGraph graph,
        int source,
        int sink,
        long targetFlow)
    {
        var sw = Stopwatch.StartNew();

        // Create a unique super source node id
        int superSourceIndex = graph.Nodes.Keys.Max() + 1;

        var maxFlowSolver = new MaxFlow();

        // add super-source ➜ real source arc
        maxFlowSolver.AddArcWithCapacity(superSourceIndex, source, targetFlow);

        // add all graph arcs
        var edgeToArc = new Dictionary<FlowEdge, int>(graph.Edges.Count);
        foreach (var edge in graph.Edges)
        {
            edgeToArc[edge] = maxFlowSolver.AddArcWithCapacity(edge.From,
                edge.To,
                edge.CurrentCapacity);
        }

        // solve
        var status = maxFlowSolver.Solve(superSourceIndex, sink);
        if (status != MaxFlow.Status.OPTIMAL)
        {
            throw new Exception($"Max-flow not optimal: {status}");
        }

        // make sure some real flow left the source
        bool hasRealFlow = Enumerable.Range(0, maxFlowSolver.NumArcs())
            .Any(a => maxFlowSolver.Tail(a) != superSourceIndex &&
                      maxFlowSolver.Flow(a) > 0);

        if (!hasRealFlow)
        {
            return 0;
        }

        long optimalFlow = maxFlowSolver.OptimalFlow();

        // write the solver’s flow back onto our edges
        foreach (var edge in graph.Edges)
        {
            long flow = maxFlowSolver.Flow(edgeToArc[edge]);

            edge.Flow += flow;
            edge.CurrentCapacity -= flow;

            if (edge.ReverseEdge is not null)
            {
                edge.ReverseEdge.CurrentCapacity += flow;
            }
        }

        sw.Stop();
        return optimalFlow;
    }
}