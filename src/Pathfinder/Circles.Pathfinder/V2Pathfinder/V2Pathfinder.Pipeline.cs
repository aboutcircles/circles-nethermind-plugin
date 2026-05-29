using System.Diagnostics;
using System.Linq;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Microsoft.Extensions.Logging;

namespace Circles.Pathfinder;

public partial class V2Pathfinder
{
    /* ======================================================================
     * Stage 2: Run max-flow solver and extract flow paths
     * ====================================================================== */

    private void SolveAndExtractPaths(PipelineContext ctx)
    {
        var solveSw = Stopwatch.StartNew();
        var solved = MaxFlowSolver.Solve(ctx.Graph.Edges, ctx.SourceId, ctx.EffectiveSinkId, ctx.Target);
        solveSw.Stop();

        ctx.SimplePaths = PathUtils.ExtractFlowPaths(solved, ctx.SourceId, ctx.EffectiveSinkId);

        {
            long solvedFlow = solved.Where(e => e.From == ctx.SourceId && e.Flow > 0).Sum(e => e.Flow);
            _logger.LogInformation("[{ReqId}] MaxFlow: flow={Flow}, edgesWithFlow={EdgesWithFlow}, paths={Paths}, solveMs={SolveMs}",
                ctx.ReqId, solvedFlow, solved.Count(e => e.Flow > 0), ctx.SimplePaths.Count, solveSw.ElapsedMilliseconds);
        }

        if (ctx.WantDebug)
            ctx.DebugStages!.RawPaths = ConvertSimplePathsToTransferSteps(ctx.SimplePaths);
    }

    /* ======================================================================
     * Stage 3: Prune paths to fit optional MaxTransfers budget
     * ====================================================================== */

    private void PruneByMaxTransfers(PipelineContext ctx)
    {
        if (!ctx.Request.MaxTransfers.HasValue || ctx.Request.MaxTransfers.Value <= 0)
            return;

        int stepCap = ctx.Request.MaxTransfers!.Value;
        int currentSteps = CountCollapsedTransferSteps(ctx.SimplePaths, ctx.Graph);

        if (currentSteps > stepCap)
            ctx.SimplePaths = PrunePathsByStepLimit(ctx.SimplePaths, stepCap, ctx.Graph);
    }

    /* ======================================================================
     * Stage 4: Convert SimpleEdge paths → FlowEdge paths
     * ====================================================================== */

    private static void ConvertToFlowEdges(PipelineContext ctx)
    {
        ctx.FlowPaths = new List<List<FlowEdge>>(ctx.SimplePaths.Count);

        foreach (var path in ctx.SimplePaths)
        {
            var list = new List<FlowEdge>(path.Count);
            foreach (var e in path)
            {
                list.Add(new FlowEdge(e.From, e.To, e.Token, e.Capacity)
                {
                    Flow = e.Flow,
                    CurrentCapacity = e.Capacity
                });
            }
            ctx.FlowPaths.Add(list);
        }
    }

    /* ======================================================================
     * Stage 5: Replace virtual-sink IDs with real sink
     * ====================================================================== */

    private static void ReplaceVirtualSink(PipelineContext ctx)
    {
        if (ctx.Graph.VirtualSinkAddress == null)
            return;

        int vs = ctx.Graph.VirtualSinkAddress.Value;
        var replaced = new List<List<FlowEdge>>(ctx.FlowPaths.Count);

        foreach (var path in ctx.FlowPaths)
        {
            var fixedPath = new List<FlowEdge>(path.Count);
            foreach (var fe in path)
            {
                int from = fe.From == vs ? ctx.SinkId : fe.From;
                int to = fe.To == vs ? ctx.SinkId : fe.To;
                fixedPath.Add(new FlowEdge(from, to, fe.Token, fe.InitialCapacity)
                {
                    Flow = fe.Flow,
                    CurrentCapacity = fe.CurrentCapacity
                });
            }
            replaced.Add(fixedPath);
        }

        ctx.FlowPaths = replaced;
    }

    /* ======================================================================
     * Stage 6: Collapse balance nodes + aggregate identical edges
     * ====================================================================== */

    private void CollapseAndAggregate(PipelineContext ctx)
    {
        var (collapsed, consentDroppedPaths) =
            CollapseBalanceNodes(ctx.FlowPaths, ctx.Graph, ctx.SourceId, ctx.SinkId);
        ctx.Aggregated = collapsed.AggregateIdenticalEdges();
        ctx.ConsentDroppedPaths = consentDroppedPaths;

        {
            int beforeCollapse = ctx.FlowPaths.Sum(p => p.Count);
            _logger.LogInformation("[{ReqId}] Collapsed: before={Before}, after={After}, droppedZero={DroppedZero}",
                ctx.ReqId, beforeCollapse, ctx.Aggregated.Edges.Count,
                ctx.Aggregated.Edges.Count(e => e.Flow <= 0));
        }

        if (ctx.WantDebug)
            ctx.DebugStages!.Collapsed = ConvertFlowEdgesToTransferSteps(ctx.Aggregated.Edges);
    }

    /* ======================================================================
     * Stage 7: Insert Router between Avatar → Group transfers
     * ====================================================================== */

    private void InsertRouterEdges(PipelineContext ctx)
    {
        ctx.Edges = InsertRouterInTransfers(ctx.Aggregated.Edges, ctx.Graph);
    }

    /* ======================================================================
     * Stage 8: Validate consented flow rules
     * ====================================================================== */

    private void ValidateConsent(PipelineContext ctx)
    {
        // Single source of truth for consent enforcement: PathHasConsentViolation /
        // PathHasConsentedIntermediary in CollapseBalanceNodes (path level, pre-aggregation).
        // No post-aggregation safety net — see 2026-04-28 decision (one check is enough).
        int routerEdges = ctx.Edges.Count - ctx.Aggregated.Edges.Count;
        _logger.LogInformation(
            "[{ReqId}] Router: +{RouterEdges} edges | Consent: mode={Mode}, pathsDropped={PathsDropped}",
            ctx.ReqId, Math.Max(0, routerEdges),
            _settings.ExcludeConsentedIntermediaries ? "exclude-intermediaries" : "validate-rules",
            ctx.ConsentDroppedPaths);

        if (ctx.WantDebug)
            ctx.DebugStages!.RouterInserted = ConvertFlowEdgesToTransferSteps(ctx.Edges);
    }

    /* ======================================================================
     * Stage 9: Sort edges for mint dependencies
     * ====================================================================== */

    private void SortForMintDependencies(PipelineContext ctx)
    {
        ctx.Edges = SortEdgesForMintDependencies(ctx.Edges, ctx.Graph);
        ValidateMintEdgeOrdering(ctx);

        {
            int groupMints = ctx.Edges.Count(e => ctx.Graph.IsGroup(e.From));
            int collateralEdges = ctx.Edges.Count(e => ctx.Graph.IsRouter(e.From) && ctx.Graph.IsGroup(e.To));
            _logger.LogInformation("[{ReqId}] MintSort: groups={Groups}, collateral={Collateral}, total={Total}",
                ctx.ReqId, groupMints, collateralEdges, ctx.Edges.Count);
        }
    }

    /* ======================================================================
     * Stage 10: Quantize sink-bound edges (invitation module)
     * ====================================================================== */

    private void Quantize(PipelineContext ctx)
    {
        if (ctx.Request.QuantizedMode != true)
            return;

        const long InvitationQuanta = 96_000_000L;
        int preQuantCount = ctx.Edges.Count;

        ctx.Edges = QuantizeSinkBoundEdgesByToken(ctx.Edges, ctx.SinkId, InvitationQuanta, ctx.Target);
        PropagateQuantizationBackwards(ctx.Edges, ctx.SinkId, ctx.SourceId);
        ValidateQuantizedSinkTransfers(ctx, InvitationQuanta);
        ctx.Edges = AddSinkSelfLoopAggregation(ctx.Edges, ctx.SinkId);

        _logger.LogInformation("[{ReqId}] Quantized: before={Before}, after={After}, quanta={Quanta}",
            ctx.ReqId, preQuantCount, ctx.Edges.Count, InvitationQuanta);

        if (ctx.WantDebug)
            ctx.DebugStages!.Sorted = ConvertFlowEdgesToTransferSteps(ctx.Edges);
    }
}
