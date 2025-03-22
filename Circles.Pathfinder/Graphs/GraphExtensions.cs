using System.Diagnostics;
using Google.OrTools.Graph;
using Circles.Pathfinder.Edges;
using Nethermind.Int256;

namespace Circles.Pathfinder.Graphs;

public static class GraphExtensions
{
    /// <summary>
    /// Computes max flow via a super source. If the network can push targetFlow, 
    /// the flow will match it exactly. Otherwise, it's lower.
    /// </summary>
    public static UInt256 ComputeMaxFlowWithPaths(
        this FlowGraph graph,
        string source,
        string sink,
        UInt256 targetFlow)
    {
        Stopwatch sw = new();
        sw.Start();

        // 1) Determine largest capacity or targetFlow
        UInt256 maxCap = UInt256.Zero;
        foreach (var e in graph.Edges)
        {
            if (e.CurrentCapacity > maxCap)
            {
                maxCap = e.CurrentCapacity;
            }
        }

        if (targetFlow > maxCap)
        {
            maxCap = targetFlow;
        }

        // 2) Shift if bitLen > 63
        int bitLen = maxCap.BitLen;
        int shift = bitLen > 63 ? bitLen - 63 : 0;

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
        UInt256 scaledTf = (shift > 0) ? (targetFlow >> shift) : targetFlow;
        long tfCapacity = scaledTf > (UInt256)long.MaxValue
            ? long.MaxValue
            : (long)scaledTf;

        if (!nodeIndices.ContainsKey(source) || !nodeIndices.ContainsKey(sink))
        {
            throw new ArgumentException("Source or sink not in node map.");
        }

        int realSourceIndex = nodeIndices[source];
        int sinkIndex = nodeIndices[sink];

        // 6) superSource->realSource arc
        maxFlowSolver.AddArcWithCapacity(superSourceIndex, realSourceIndex, tfCapacity);

        // 7) Add arcs for each edge
        var edgeToArc = new Dictionary<FlowEdge, int>();
        foreach (var edge in graph.Edges)
        {
            int fromIdx = nodeIndices[edge.From];
            int toIdx = nodeIndices[edge.To];

            UInt256 scaledCap = (shift > 0)
                ? (edge.CurrentCapacity >> shift)
                : edge.CurrentCapacity;

            long cap = scaledCap > (UInt256)long.MaxValue
                ? long.MaxValue
                : (long)scaledCap;

            edgeToArc[edge] = maxFlowSolver.AddArcWithCapacity(fromIdx, toIdx, cap);
        }

        // 8) Solve from superSource->sink
        var status = maxFlowSolver.Solve(superSourceIndex, sinkIndex);
        if (status != MaxFlow.Status.OPTIMAL)
        {
            throw new Exception($"Max flow not optimal: {status}");
        }

        long scaledFlowVal = maxFlowSolver.OptimalFlow();
        UInt256 realFlow = shift > 0
            ? (UInt256)scaledFlowVal << shift
            : (UInt256)scaledFlowVal;

        // 9) Store each edge’s flow
        foreach (var edge in graph.Edges)
        {
            long arcFlow = maxFlowSolver.Flow(edgeToArc[edge]);
            UInt256 realEdgeFlow = shift > 0
                ? (UInt256)arcFlow << shift
                : (UInt256)arcFlow;

            edge.Flow += realEdgeFlow; // accumulate flow
            edge.CurrentCapacity -= realEdgeFlow;
            if (edge.ReverseEdge != null)
            {
                edge.ReverseEdge.CurrentCapacity += realEdgeFlow;
            }
        }

        sw.Stop();
        Console.WriteLine($"TIMING: GraphExtensions.ComputeMaxFlowWithPaths: {sw.ElapsedMilliseconds}ms");

        return realFlow;
    }
}