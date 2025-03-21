using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Circles.Pathfinder.Edges;
using Google.OrTools.Graph;

namespace Circles.Pathfinder.Graphs;

public static class GraphExtensions
{
    /// <summary>
    /// Computes max flow from source to sink using Google's OR-Tools (long capacities).
    /// - We scale capacities & targetFlow if > 2^63-1 using a bit shift.
    /// - OR-Tools gives us a "raw" flow, possibly bigger than requested targetFlow.
    /// - We clamp to EXACTLY targetFlow by distributing leftover among edges
    ///   using purely BigInteger operations (no double floating rounding).
    /// </summary>
    public static BigInteger ComputeMaxFlowWithPaths(
        this FlowGraph graph,
        string source,
        string sink,
        BigInteger targetFlow)
    {
        //-------------------------------------------------------------
        // 1) Determine largest capacity or targetFlow
        //-------------------------------------------------------------
        BigInteger maxCap = BigInteger.Zero;
        foreach (var edge in graph.Edges)
        {
            if (edge.CurrentCapacity > maxCap) maxCap = edge.CurrentCapacity;
        }

        if (targetFlow > maxCap)
        {
            maxCap = targetFlow;
        }

        long bitLength64 = maxCap.GetBitLength();
        if (bitLength64 > int.MaxValue)
        {
            throw new InvalidOperationException("Bit length exceeds integer range.");
        }

        int bitLength = (int)bitLength64;
        int shift = (bitLength > 63) ? (bitLength - 63) : 0;

        //-------------------------------------------------------------
        // 2) Build node index map
        //-------------------------------------------------------------
        var nodeIndices = new Dictionary<string, int>();
        int nodeIndex = 0;
        foreach (var node in graph.Nodes.Values)
        {
            nodeIndices[node.Address] = nodeIndex++;
        }

        var maxFlow = new MaxFlow();

        //-------------------------------------------------------------
        // 3) Add arcs with scaled capacities
        //-------------------------------------------------------------
        var edgeToArc = new Dictionary<FlowEdge, int>();
        foreach (var edge in graph.Edges)
        {
            int fromIndex = nodeIndices[edge.From];
            int toIndex = nodeIndices[edge.To];

            // shift capacity
            BigInteger scaledCapBI = (shift > 0)
                ? (edge.CurrentCapacity >> shift)
                : edge.CurrentCapacity;

            long capacity;
            if (scaledCapBI > long.MaxValue)
            {
                capacity = long.MaxValue;
                Console.WriteLine($"Scaled capacity {scaledCapBI} too big, clamped to {capacity}");
            }
            else
            {
                capacity = (long)scaledCapBI;
            }

            int arc = maxFlow.AddArcWithCapacity(fromIndex, toIndex, capacity);
            edgeToArc[edge] = arc;
        }

        //-------------------------------------------------------------
        // 4) Scale down targetFlow for reference in solver
        //-------------------------------------------------------------
        BigInteger scaledTargetFlowBI = (shift > 0)
            ? (targetFlow >> shift)
            : targetFlow;

        long scaledTargetFlow = (scaledTargetFlowBI > long.MaxValue)
            ? long.MaxValue
            : (long)scaledTargetFlowBI;

        //-------------------------------------------------------------
        // 5) Solve the max flow in OR-Tools
        //-------------------------------------------------------------
        if (!nodeIndices.ContainsKey(source))
        {
            throw new Exception($"Source not found in nodeIndices: {source}");
        }
        if (!nodeIndices.ContainsKey(sink))
        {
            throw new Exception($"Sink not found in nodeIndices: {sink}");
        }

        int sourceIndex = nodeIndices[source];
        int sinkIndex = nodeIndices[sink];
        var status = maxFlow.Solve(sourceIndex, sinkIndex);
        if (status != MaxFlow.Status.OPTIMAL)
        {
            throw new Exception("Max flow could not find an optimal solution: " + status);
        }

        long scaledFlowValue = maxFlow.OptimalFlow(); // in scaled units

        // The "raw" solver flow in unscaled units:
        BigInteger rawRealFlow = (shift > 0)
            ? ((BigInteger)scaledFlowValue << shift)
            : (BigInteger)scaledFlowValue;

        Console.WriteLine($"OR-Tools raw scaled flow = {scaledFlowValue}, unscaled = {rawRealFlow}");

        //-------------------------------------------------------------
        // 6) finalFlow = min(rawRealFlow, targetFlow) => we want EXACT
        //-------------------------------------------------------------
        // If rawRealFlow < targetFlow, we can't magically push more.
        // If rawRealFlow >= targetFlow, we clamp to exactly targetFlow.
        BigInteger finalFlow = (rawRealFlow < targetFlow)
            ? rawRealFlow
            : targetFlow;

        // We'll do a ratio if rawRealFlow > finalFlow, but purely in BigInteger
        // ratio = finalFlow / rawRealFlow as fraction => (num, den) = (finalFlow, rawRealFlow)
        BigInteger ratioNum = finalFlow;     // numerator
        BigInteger ratioDen = rawRealFlow;   // denominator

        //-------------------------------------------------------------
        // 7) Gather raw flows from solver, store fraction remainders
        //-------------------------------------------------------------
        var flowData = new List<FlowItem>();
        BigInteger sumFloors = BigInteger.Zero;

        // read each edge's OR-Tools flow, up-scale
        foreach (var edge in graph.Edges)
        {
            int arc = edgeToArc[edge];
            long arcFlow = maxFlow.Flow(arc); // scaled

            // rawFlow = up-scaled solver flow for this edge
            BigInteger rawEdgeFlow = (shift > 0)
                ? ((BigInteger)arcFlow << shift)
                : (BigInteger)arcFlow;

            if (rawEdgeFlow == 0)
            {
                flowData.Add(new FlowItem(edge, 0, BigInteger.Zero, BigInteger.Zero));
                continue;
            }

            // If rawRealFlow <= finalFlow => ratio = 1 => floorVal = rawEdgeFlow
            // else => floorVal = (rawEdgeFlow * ratioNum) / ratioDen
            // remainder = (rawEdgeFlow * ratioNum) mod ratioDen => used to sort edges
            if (ratioDen == 0) // if rawRealFlow was 0, can't happen here
            {
                // fallback
                flowData.Add(new FlowItem(edge, rawEdgeFlow, BigInteger.Zero, rawEdgeFlow));
            }
            else
            {
                BigInteger numerator = rawEdgeFlow * ratioNum;
                BigInteger floorVal = numerator / ratioDen;  // integer division
                BigInteger remainder = numerator % ratioDen; // for leftover distribution

                flowData.Add(new FlowItem(edge, rawEdgeFlow, remainder, floorVal));
                sumFloors += floorVal;
            }
        }

        //-------------------------------------------------------------
        // sumFloors now is the total flow after integer floor. We might be short
        // leftover = finalFlow - sumFloors
        //-------------------------------------------------------------
        BigInteger leftover = finalFlow - sumFloors;
        if (leftover < 0) leftover = 0; // can't happen if ratio is correct, but just in case

        //-------------------------------------------------------------
        // 8) Distribute leftover among edges with largest remainder
        //-------------------------------------------------------------
        // We can't exceed the original rawEdgeFlow from the solver though.
        // So if item.FloorVal < item.RawFlow, we can increment by 1.
        // Sort by remainder desc (largest first).
        //-------------------------------------------------------------
        // We do a stable sort so edges with equal remainder keep their order,
        // or we can just do a descending compare. Doesn't matter typically.
        flowData.Sort((a, b) => b.Remainder.CompareTo(a.Remainder));

        for (int i = 0; i < flowData.Count && leftover > 0; i++)
        {
            var fi = flowData[i];
            // If floorVal < rawFlow, we can increment by 1
            if (fi.FloorVal < fi.RawFlow)
            {
                fi.FloorVal += 1;
                leftover -= 1;
                flowData[i] = fi; // reassign
            }
        }

        //-------------------------------------------------------------
        // 9) Write final flows into edges
        //-------------------------------------------------------------
        // sum of these flows = sumFloors + leftoverUsed = finalFlow exactly (when raw>= target).
        foreach (var fi in flowData)
        {
            BigInteger flowVal = fi.FloorVal;

            fi.Edge.Flow = flowVal;
            fi.Edge.CurrentCapacity -= flowVal;

            if (fi.Edge.ReverseEdge != null)
            {
                fi.Edge.ReverseEdge.CurrentCapacity += flowVal;
            }
        }

        Console.WriteLine($"finalFlow = {finalFlow}, leftover after distribution = {leftover}");
        return finalFlow;
    }

    private struct FlowItem
    {
        public FlowEdge Edge { get; }
        public BigInteger RawFlow { get; set; }
        public BigInteger Remainder { get; set; }
        public BigInteger FloorVal { get; set; }

        public FlowItem(
            FlowEdge edge,
            BigInteger rawFlow,
            BigInteger remainder,
            BigInteger floorVal
        )
        {
            Edge = edge;
            RawFlow = rawFlow;
            Remainder = remainder;
            FloorVal = floorVal;
        }
    }
}