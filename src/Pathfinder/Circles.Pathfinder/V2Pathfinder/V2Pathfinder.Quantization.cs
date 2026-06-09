using System.Linq;
using Circles.Common;
using Circles.Pathfinder.Edges;
using Microsoft.Extensions.Logging;

namespace Circles.Pathfinder;

public partial class V2Pathfinder
{
    /* ------------------------------------------------------------------------
     * Quantize sink-bound edges by token type for the invitation module.
     *
     * Algorithm:
     * 1. Separate edges into sink-bound and non-sink-bound
     * 2. Group sink-bound edges by token type
     * 3. For each token type:
     *    - Sum total flow to sink
     *    - Calculate quantized amount: floor(total / quantaSize) * quantaSize
     *    - Proportionally scale each edge's flow to fit the quantized total
     * 4. Return non-sink-bound edges + quantized sink-bound edges
     *
     * This allows multiple small transfers of the same token to combine into
     * valid 96 CRC quanta (e.g., 60 CRC + 36 CRC = 96 CRC quantum).
     * --------------------------------------------------------------------- */
    private List<FlowEdge> QuantizeSinkBoundEdgesByToken(
        List<FlowEdge> edges,
        int sinkId,
        long quantaSize,
        long targetFlow)
    {
        // Separate sink-bound from other edges
        var sinkBound = new List<FlowEdge>();
        var nonSinkBound = new List<FlowEdge>();

        foreach (var edge in edges)
        {
            if (edge.To == sinkId && edge.Flow > 0)
                sinkBound.Add(edge);
            else
                nonSinkBound.Add(edge);
        }

        // If no sink-bound edges, nothing to quantize
        if (sinkBound.Count == 0)
            return edges;

        // Group sink-bound edges by token type
        var byToken = new Dictionary<int, List<FlowEdge>>();
        foreach (var edge in sinkBound)
        {
            if (!byToken.TryGetValue(edge.Token, out var list))
            {
                list = new List<FlowEdge>();
                byToken[edge.Token] = list;
            }
            list.Add(edge);
        }

        // Calculate how many quanta we need (based on target flow)
        long targetQuanta = targetFlow / quantaSize;
        long quantaRemaining = targetQuanta;

        // Process each token type and quantize
        var quantizedSinkBound = new List<FlowEdge>();

        // Sort token groups by total flow descending (prefer larger flows first)
        var tokenGroups = byToken
            .Select(kvp => (Token: kvp.Key, Edges: kvp.Value, Total: kvp.Value.Sum(e => e.Flow)))
            .OrderByDescending(g => g.Total)
            .ToList();

        foreach (var group in tokenGroups)
        {
            if (quantaRemaining <= 0)
                break;

            long totalFlow = group.Total;

            // How many full quanta can this token type provide?
            long availableQuanta = totalFlow / quantaSize;

            if (availableQuanta <= 0)
                continue; // This token type can't provide even 1 quantum

            // Take only what we need (up to what's available)
            long quantaToUse = Math.Min(availableQuanta, quantaRemaining);
            long quantizedTotal = quantaToUse * quantaSize;

            // Proportionally scale each edge's flow
            // Use integer math to avoid floating point issues
            long allocated = 0;

            for (int i = 0; i < group.Edges.Count; i++)
            {
                var edge = group.Edges[i];
                long scaledFlow;

                if (i == group.Edges.Count - 1)
                {
                    // Last edge gets remainder to ensure exact total
                    scaledFlow = quantizedTotal - allocated;
                    if (scaledFlow <= 0)
                    {
                        _logger.LogWarning("[QuantizeSinkBoundEdges] Last edge remainder is {ScaledFlow} " +
                            "(quantizedTotal={QuantizedTotal}, allocated={Allocated}, edges={EdgeCount}) — rounding consumed entire quantum",
                            scaledFlow, quantizedTotal, allocated, group.Edges.Count);
                        continue; // Skip this edge — earlier edges consumed everything
                    }
                }
                else
                {
                    // Proportional allocation: edge.Flow / totalFlow * quantizedTotal
                    scaledFlow = (edge.Flow * quantizedTotal) / totalFlow;
                }

                if (scaledFlow > 0)
                {
                    var quantizedEdge = new FlowEdge(edge.From, edge.To, edge.Token, edge.InitialCapacity)
                    {
                        Flow = scaledFlow,
                        CurrentCapacity = edge.CurrentCapacity
                    };
                    quantizedSinkBound.Add(quantizedEdge);
                    allocated += scaledFlow;
                }
            }

            quantaRemaining -= quantaToUse;
        }

        // Combine non-sink-bound edges with quantized sink-bound edges
        var result = new List<FlowEdge>(nonSinkBound.Count + quantizedSinkBound.Count);
        result.AddRange(nonSinkBound);
        result.AddRange(quantizedSinkBound);

        return result;
    }

    /* ------------------------------------------------------------------------
     * After quantizing sink-bound edges, upstream edges still carry the original
     * (pre-quantization) flow. This creates nettedFlow mismatches at intermediate
     * vertices (e.g., Group receives 300 collateral but only mints 288).
     *
     * Fix: BFS backwards from sink, at each vertex scale incoming edges to match
     * the (now-reduced) total outflow. This preserves flow conservation everywhere.
     * --------------------------------------------------------------------- */
    internal static void PropagateQuantizationBackwards(List<FlowEdge> edges, int sinkId, int sourceId)
    {
        // Build vertex → edge index maps
        var incomingEdges = new Dictionary<int, List<int>>();  // vertex → indices of edges going INTO it
        var outgoingEdges = new Dictionary<int, List<int>>();  // vertex → indices of edges going OUT of it

        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            if (e.Flow <= 0) continue;

            if (!incomingEdges.TryGetValue(e.To, out var inList))
            {
                inList = new List<int>();
                incomingEdges[e.To] = inList;
            }
            inList.Add(i);

            if (!outgoingEdges.TryGetValue(e.From, out var outList))
            {
                outList = new List<int>();
                outgoingEdges[e.From] = outList;
            }
            outList.Add(i);
        }

        // Phase 1: BFS backwards from sink to discover all reachable vertices.
        var queue = new Queue<int>();
        var visited = new HashSet<int>();

        // Seed with vertices that have edges to the sink (excluding sink self-loops)
        if (incomingEdges.TryGetValue(sinkId, out var sinkFeederIndices))
        {
            foreach (int idx in sinkFeederIndices)
            {
                int feeder = edges[idx].From;
                if (feeder != sinkId)
                    queue.Enqueue(feeder);
            }
        }

        // First pass: BFS discover + adjust
        while (queue.Count > 0)
        {
            int vertex = queue.Dequeue();
            if (!visited.Add(vertex)) continue;
            if (vertex == sourceId) continue;

            if (!incomingEdges.TryGetValue(vertex, out var inIndices) || inIndices.Count == 0)
                continue;

            AdjustVertexInflow(edges, vertex, inIndices,
                outgoingEdges.GetValueOrDefault(vertex), sourceId);

            // Enqueue upstream vertices for discovery
            foreach (int idx in inIndices)
            {
                int from = edges[idx].From;
                if (from != sourceId && !visited.Contains(from))
                    queue.Enqueue(from);
            }
        }

        // Phase 2: Convergence passes over all discovered vertices.
        // The BFS above may process a vertex before all its downstream successors
        // have been adjusted (e.g., group G feeds both sink directly and avatar M
        // indirectly — if G is dequeued before M, G's collateral scaling uses stale
        // G→M flow). Re-pass until no vertex needs further adjustment.
        // Convergence is guaranteed: each pass only reduces flows (non-negative integers).
        const int maxPasses = 20; // Safety limit; practical convergence in 1–3 passes
        for (int pass = 0; pass < maxPasses; pass++)
        {
            bool changed = false;
            foreach (int vertex in visited)
            {
                if (vertex == sourceId) continue;
                if (!incomingEdges.TryGetValue(vertex, out var inIndices) || inIndices.Count == 0)
                    continue;

                if (AdjustVertexInflow(edges, vertex, inIndices,
                        outgoingEdges.GetValueOrDefault(vertex), sourceId))
                    changed = true;
            }

            if (!changed) break;
        }
    }

    /// <summary>
    /// Scale incoming edges of a vertex to match its (now-reduced) total outflow.
    /// Returns true if any edge flow was actually changed.
    /// </summary>
    private static bool AdjustVertexInflow(
        List<FlowEdge> edges,
        int vertex,
        List<int> inIndices,
        List<int>? outIndices,
        int sourceId)
    {
        // Compute token types and totals
        var inTokenTypes = new HashSet<int>();
        long totalIn = 0;
        foreach (int idx in inIndices)
        {
            inTokenTypes.Add(edges[idx].Token);
            totalIn += edges[idx].Flow;
        }

        var outTokenTypes = new HashSet<int>();
        long totalOut = 0;
        if (outIndices != null)
        {
            foreach (int idx in outIndices)
            {
                outTokenTypes.Add(edges[idx].Token);
                totalOut += edges[idx].Flow;
            }
        }

        if (totalIn <= totalOut) return false; // Already balanced

        // Token conversion: outflow contains token types not present in inflow.
        // This happens at group minting vertices (collateral in → group token out).
        // When quantization zeroes a token, in={A,B} out={A} — out IS a subset of in,
        // so per-token scaling correctly handles it (token B gets scaled to 0).
        bool isTokenConverting = !outTokenTypes.IsSubsetOf(inTokenTypes);

        if (isTokenConverting)
        {
            // Token-converting vertex (group minting): use total-based scaling.
            // Per-token conservation doesn't apply here — token types change.
            long allocated = 0;
            for (int j = 0; j < inIndices.Count; j++)
            {
                int idx = inIndices[j];
                var e = edges[idx];
                long newFlow;

                if (j == inIndices.Count - 1)
                    newFlow = totalOut - allocated;
                else
                    newFlow = totalIn > 0 ? (e.Flow * totalOut) / totalIn : 0;

                if (newFlow < 0) newFlow = 0;
                e.Flow = newFlow;
                allocated += newFlow;
            }
        }
        else
        {
            // Same token types in/out: scale per-token independently.
            // This preserves per-token flow conservation — total-based scaling
            // would redistribute flow between token types, violating Hub.sol's
            // per-token NettedFlowMismatch check.
            var outflowByToken = new Dictionary<int, long>();
            if (outIndices != null)
            {
                foreach (int idx in outIndices)
                {
                    var e = edges[idx];
                    outflowByToken.TryGetValue(e.Token, out long current);
                    outflowByToken[e.Token] = current + e.Flow;
                }
            }

            var inByToken = new Dictionary<int, List<int>>();
            foreach (int idx in inIndices)
            {
                int token = edges[idx].Token;
                if (!inByToken.TryGetValue(token, out var list))
                {
                    list = new List<int>();
                    inByToken[token] = list;
                }
                list.Add(idx);
            }

            foreach (var (token, tokenInIndices) in inByToken)
            {
                outflowByToken.TryGetValue(token, out long tokenOut);

                long tokenIn = 0;
                foreach (int idx in tokenInIndices)
                    tokenIn += edges[idx].Flow;

                if (tokenIn <= tokenOut) continue;

                long allocated = 0;
                for (int j = 0; j < tokenInIndices.Count; j++)
                {
                    int idx = tokenInIndices[j];
                    var e = edges[idx];
                    long newFlow;

                    if (j == tokenInIndices.Count - 1)
                        newFlow = tokenOut - allocated;
                    else
                        newFlow = tokenIn > 0 ? (e.Flow * tokenOut) / tokenIn : 0;

                    if (newFlow < 0) newFlow = 0;
                    e.Flow = newFlow;
                    allocated += newFlow;
                }
            }
        }

        return true;
    }

    /* ------------------------------------------------------------------------
     * Validate that the TOTAL flow per token type to sink is a multiple of quantaSize.
     * Individual edges may have non-quantized flows, but their sum per token must be.
     * Used as a safety check after quantization to ensure correctness.
     * --------------------------------------------------------------------- */
    private void ValidateQuantizedSinkTransfers(PipelineContext ctx, long quantaSize)
    {
        var edges = ctx.Edges;
        int sinkId = ctx.SinkId;
        // Group sink-bound edges by token and sum flows
        var flowByToken = new Dictionary<int, long>();

        foreach (var edge in edges)
        {
            if (edge.To != sinkId || edge.Flow <= 0)
                continue;

            flowByToken.TryGetValue(edge.Token, out long current);
            flowByToken[edge.Token] = current + edge.Flow;
        }

        // Validate each token type's total is a multiple of quantaSize
        foreach (var (token, totalFlow) in flowByToken)
        {
            bool isQuantized = totalFlow % quantaSize == 0;
            if (!isQuantized)
            {
                string tokenAddr = AddressIdPool.StringOf(token);
                _logger.LogError(
                    "[{ReqId}] Quantization violation: Total flow of token {Token} to sink is {Flow}, " +
                    "not a multiple of {Quanta} (96 CRC).",
                    ctx.ReqId, tokenAddr, totalFlow, quantaSize);
                ctx.Edges.Clear();
                return;
            }
        }
    }

    /* ------------------------------------------------------------------------
     * Add self-loop aggregation edges for quantizedMode responses.
     *
     * In quantizedMode, the response includes Sink → Sink edges that aggregate
     * the total flow per token type. This provides a convenient summary showing
     * what tokens and how much of each the sink receives in the quantized flow.
     *
     * Example output edge: { From: sink, To: sink, Token: tokenOwner, Flow: total }
     * This allows clients to easily determine which tokens were delivered.
     * --------------------------------------------------------------------- */
    private static List<FlowEdge> AddSinkSelfLoopAggregation(List<FlowEdge> edges, int sinkId)
    {
        // Group sink-bound edges by token, sum flows
        var tokenFlows = new Dictionary<int, long>();

        foreach (var edge in edges)
        {
            if (edge.To != sinkId || edge.Flow <= 0)
                continue;

            tokenFlows.TryGetValue(edge.Token, out long current);
            tokenFlows[edge.Token] = current + edge.Flow;
        }

        // No sink-bound edges means nothing to aggregate
        if (tokenFlows.Count == 0)
            return edges;

        // Append self-loop for each token: {Sink → Sink, tokenOwner, total}
        var result = new List<FlowEdge>(edges.Count + tokenFlows.Count);
        result.AddRange(edges);

        foreach (var (token, total) in tokenFlows)
        {
            result.Add(new FlowEdge(sinkId, sinkId, token, total)
            {
                Flow = total,
                CurrentCapacity = 0
            });
        }

        return result;
    }
}
