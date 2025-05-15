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
        using var maxFlow = new MaxFlow();

        /* ----------------------------------------------------------------
         * Arc-id 0      : super-source ➜ real source
         * Arc-id 1..N   : real capacity edges (same order as edgesList)
         * ---------------------------------------------------------------- */
        int superSource = capacityEdges.Count > 0
            ? Math.Max(capacityEdges.Max(e => Math.Max(e.From, e.To)), sourceAvatar) + 1
            : sourceAvatar + 1;

        maxFlow.AddArcWithCapacity(superSource, sourceAvatar, targetFlow);

        /* Add the real arcs — their ids are predictable: offset + index  */
        for (int i = 0; i < capacityEdges.Count; i++)
        {
            var edge = capacityEdges[i];
            maxFlow.AddArcWithCapacity(edge.From, edge.To, edge.InitialCapacity);
        }

        /* --------------------------- solve ------------------------------ */
        var status = maxFlow.Solve(superSource, sinkAvatar);
        if (status != MaxFlow.Status.OPTIMAL)
            throw new InvalidOperationException($"Max-flow failed: {status}");

        /* ---------------- write back only the arcs with flow ------------ */
        const int arcOffset = 1; // skip the super-source arc (id 0)
        var result = new List<SimpleEdge>();

        for (int i = 0; i < capacityEdges.Count; i++)
        {
            long flow = maxFlow.Flow(arcOffset + i);
            bool hasFlow = flow > 0;
            if (!hasFlow)
            {
                continue;
            }

            var edge = capacityEdges[i];
            long remain = edge.InitialCapacity - flow;

            result.Add(new SimpleEdge(edge.From, edge.To, edge.Token, remain)
            {
                Flow = flow
            });
        }

        return result;
    }
}