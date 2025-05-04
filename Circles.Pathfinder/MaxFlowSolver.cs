using Circles.Pathfinder.Edges;
using Google.OrTools.Graph;

namespace Circles.Pathfinder;

internal static class MaxFlowSolver
{
    public static IReadOnlyList<SimpleEdge> Solve(
        List<CapacityEdge> capacityEdges,
        int sourceAvatar,
        int sinkAvatar,
        long targetFlow)
    {
        var maxFlow = new MaxFlow();
        var arcOfEdge = new Dictionary<CapacityEdge, int>();

        /* add super-source */
        int superSource = capacityEdges.Max(e => Math.Max(e.From, e.To)) + 1;
        maxFlow.AddArcWithCapacity(superSource, sourceAvatar, targetFlow);

        /* add real arcs */
        foreach (CapacityEdge edge in capacityEdges)
        {
            int arcId = maxFlow.AddArcWithCapacity(edge.From, edge.To, edge.InitialCapacity);
            arcOfEdge[edge] = arcId;
        }

        /* solve */
        var status = maxFlow.Solve(superSource, sinkAvatar);
        if (status != MaxFlow.Status.OPTIMAL)
        {
            throw new InvalidOperationException($"Max-flow failed: {status}");
        }

        /* write back */
        var result = new List<SimpleEdge>();
        foreach ((CapacityEdge ce, int arc) in arcOfEdge)
        {
            long flow = maxFlow.Flow(arc);
            if (flow == 0)
            {
                continue;
            }

            long remainingCapacity = ce.InitialCapacity - flow;
            result.Add(new SimpleEdge(ce.From, ce.To, ce.Token, remainingCapacity)
            {
                Flow = flow
            });
        }

        return result;
    }
}