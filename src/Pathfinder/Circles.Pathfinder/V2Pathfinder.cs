using System.Diagnostics;
using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Int256;

namespace Circles.Pathfinder;

// The pathfinding pipeline is split across several files for maintainability.
// Each partial contributes one stage (or a related group of helpers):
//   - V2Pathfinder.cs              (this file) — class shell, entry points, ResolveAndGuard, PipelineContext
//   - V2Pathfinder/Pipeline.cs     — Stages 2–10 orchestration methods
//   - V2Pathfinder/Response.cs     — Stage 11/12: BuildTransferDtos, BuildResponse, debug-step converters
//   - V2Pathfinder/Collapse.cs     — CollapseBalanceNodes, consent filters, InsertRouterInTransfers
//   - V2Pathfinder/PathSteps.cs    — MaxTransfers budget pruning + path/transfer collapse helpers
//   - V2Pathfinder/MintOrdering.cs — SortEdgesForMintDependencies + invariant validator
//   - V2Pathfinder/Quantization.cs — invitation-quanta scaling + flow-conservation propagation
public partial class V2Pathfinder
{
    private readonly ILogger _logger;
    private readonly Settings _settings;

    /// <summary>Strip CR/LF from values before logging (defense-in-depth against log forging).</summary>
    private static string? Sanitize(string? v) => v?.Replace("\r", "").Replace("\n", "");

    public V2Pathfinder(ILogger<V2Pathfinder>? logger = null, Settings? settings = null)
    {
        _logger = logger ?? NullLogger<V2Pathfinder>.Instance;
        _settings = settings ?? new Settings();
    }

    public long ComputeMaxFlow(
        CapacityGraph capacityGraph,
        FlowRequest flowRequest,
        UInt256 targetFlow)
    {
        /* --------------------------------------------------------------------
         * 1. Resolve ids and basic guards
         * ------------------------------------------------------------------ */
        int sinkId = AddressIdPool.IdOf(flowRequest.Sink ?? throw new ArgumentNullException(nameof(flowRequest.Sink)));
        int? vs = capacityGraph.VirtualSinkAddress;
        int effSink = vs ?? sinkId;
        int sourceId = AddressIdPool.IdOf(flowRequest.Source ?? throw new ArgumentNullException(nameof(flowRequest.Source)));

        if (!capacityGraph.AvatarNodes.ContainsKey(sourceId))
        {
            _logger.LogWarning("ComputeMaxFlow: Source '{Source}' not in graph snapshot — returning zero", flowRequest.Source);
            return 0;
        }

        if (!capacityGraph.AvatarNodes.ContainsKey(effSink))
        {
            _logger.LogWarning("ComputeMaxFlow: Sink '{Sink}' not in graph snapshot — returning zero", flowRequest.Sink);
            return 0;
        }

        /* --------------------------------------------------------------------
         * 2. Run max-flow (no path extraction needed)
         * ------------------------------------------------------------------ */
        long target = CirclesConverter.TruncateToInt64(targetFlow);
        var solved = MaxFlowSolver.Solve(
            capacityGraph.Edges,
            sourceId,
            effSink,
            target);

        /* --------------------------------------------------------------------
         * 3. Sum outbound flow from the source
         * ------------------------------------------------------------------ */
        long totalFlow = 0;

        for (int i = 0; i < solved.Count; i++)
        {
            var edge = solved[i];

            bool edgeLeavesSource = edge.From == sourceId;
            bool edgeHasPositiveFlow = edge.Flow > 0;

            if (edgeLeavesSource && edgeHasPositiveFlow)
            {
                totalFlow += edge.Flow;
            }
        }

        return totalFlow;
    }

    public MaxFlowResponse ComputeMaxFlowWithPath(
        CapacityGraph capacityGraph,
        FlowRequest request,
        UInt256 targetFlow)
    {
        var ctx = ResolveAndGuard(capacityGraph, request, targetFlow);
        if (ctx is null)
            return new MaxFlowResponse("0", new List<TransferPathStep>(), null);

        // Hub.sol invariant: if advancedUsageFlags[from]==true then advancedUsageFlags[to]==true is required
        // for every flow edge (Hub.sol:668-676 isPermittedFlow). By induction, a consented source can ONLY
        // reach consented sinks. Skip the solver entirely — any path it found would be rejected by the
        // path-level filter or the audit safety net anyway.
        if (capacityGraph.ConsentedAvatars.Contains(ctx.SourceId)
            && !capacityGraph.ConsentedAvatars.Contains(ctx.SinkId))
        {
            _logger.LogInformation(
                "[{ReqId}] Consented source → non-consented sink — no path possible per Hub.sol isPermittedFlow",
                ctx.ReqId);
            return new MaxFlowResponse("0", new List<TransferPathStep>(), null)
            {
                ReqId = ctx.ReqId,
                GraphBlock = capacityGraph.Block
            };
        }

        SolveAndExtractPaths(ctx);
        PruneByMaxTransfers(ctx);
        ConvertToFlowEdges(ctx);
        ReplaceVirtualSink(ctx);
        CollapseAndAggregate(ctx);
        InsertRouterEdges(ctx);
        ValidateConsent(ctx);
        SortForMintDependencies(ctx);
        Quantize(ctx);
        BuildTransferDtos(ctx);
        return BuildResponse(ctx);
    }

    /* ======================================================================
     * Pipeline context — carries data between stages
     * ====================================================================== */

    private sealed class PipelineContext
    {
        // Immutable inputs
        public required CapacityGraph Graph { get; init; }
        public required FlowRequest Request { get; init; }
        public required int SourceId { get; init; }
        public required int SinkId { get; init; }
        public required int EffectiveSinkId { get; init; }
        public required long Target { get; init; }
        public required string ReqId { get; init; }
        public required Stopwatch TotalStopwatch { get; init; }
        public required bool WantDebug { get; init; }

        // Mutable state flowing through stages
        public List<List<SimpleEdge>> SimplePaths { get; set; } = new();
        public List<List<FlowEdge>> FlowPaths { get; set; } = new();
        public FlowGraph Aggregated { get; set; } = null!;
        public List<FlowEdge> Edges { get; set; } = new();
        public List<TransferPathStep> Transfers { get; set; } = new();
        public int ConsentDroppedPaths { get; set; }
        public DebugPipelineStages? DebugStages { get; set; }

        // Canary: HubContractValidator results (runs on every response, observe-only)
        public int ValidationErrors { get; set; }
        public IReadOnlyList<string>? ValidationViolationRules { get; set; }
        public bool ValidatorException { get; set; }

        // Warning-severity validator violations: logged + metricised but NEVER block
        // the response (in contrast to errors, which the host replaces with empty
        // results in FindPathHandler.cs).
        public int ValidationWarnings { get; set; }
        public IReadOnlyList<string>? ValidationWarningRules { get; set; }
    }

    /* ======================================================================
     * Stage 1: Resolve IDs and validate source/sink
     * ====================================================================== */

    private PipelineContext? ResolveAndGuard(
        CapacityGraph capacityGraph,
        FlowRequest request,
        UInt256 targetFlow)
    {
        int sinkId = AddressIdPool.IdOf(request.Sink ?? throw new ArgumentNullException(nameof(request.Sink)));
        int effSink = capacityGraph.VirtualSinkAddress ?? sinkId;
        int sourceId = AddressIdPool.IdOf(request.Source ?? throw new ArgumentNullException(nameof(request.Source)));

        var reqId = Guid.NewGuid().ToString("N")[..8];

        if (!capacityGraph.AvatarNodes.ContainsKey(sourceId))
        {
            _logger.LogWarning("[{ReqId}] Source '{Source}' not in graph snapshot (block={Block}) — returning zero flow",
                reqId, Sanitize(request.Source), capacityGraph.Block);
            return null;
        }
        if (!capacityGraph.AvatarNodes.ContainsKey(effSink))
        {
            _logger.LogWarning("[{ReqId}] Sink '{Sink}' not in graph snapshot (block={Block}) — returning zero flow",
                reqId, Sanitize(request.Sink), capacityGraph.Block);
            return null;
        }

        // Replay log: everything needed to reconstruct this request against the test environment
        {
            var flags = new List<string>(4);
            if (request.WithWrap == true) flags.Add("withWrap");
            if (request.QuantizedMode == true) flags.Add("quantized");
            if (request.MaxTransfers.HasValue) flags.Add($"maxTransfers={request.MaxTransfers}");
            if (request.FromTokens?.Count > 0) flags.Add($"fromTokens={request.FromTokens.Count}");
            if (request.ToTokens?.Count > 0) flags.Add($"toTokens={request.ToTokens.Count}");
            if (request.ExcludedFromTokens?.Count > 0) flags.Add($"exclFrom={request.ExcludedFromTokens.Count}");
            if (request.ExcludedToTokens?.Count > 0) flags.Add($"exclTo={request.ExcludedToTokens.Count}");
            if (request.SimulatedBalances?.Count > 0) flags.Add($"simBal={request.SimulatedBalances.Count}");
            if (request.SimulatedTrusts?.Count > 0) flags.Add($"simTrust={request.SimulatedTrusts.Count}");

            _logger.LogInformation(
                "[{ReqId}] Request: from={Source} to={Sink} amount={Amount} block={Block} flags=[{Flags}]",
                reqId, Sanitize(request.Source), Sanitize(request.Sink), targetFlow, capacityGraph.Block,
                flags.Count > 0 ? string.Join(",", flags) : "none");
        }

        _logger.LogInformation("[{ReqId}] Graph: avatars={Avatars}, groups={Groups}, edges={Edges}",
            reqId, capacityGraph.AvatarNodes.Count, capacityGraph.GroupNodes.Count, capacityGraph.Edges.Count);

        if (capacityGraph.GroupNodes.Count == 0)
        {
            _logger.LogWarning("[{ReqId}] Graph has 0 groups — group minting paths will be unavailable. " +
                "Check group loading (SQL fallback may have broken filter, or Cache Service not returning groups).", reqId);
        }

        return new PipelineContext
        {
            Graph = capacityGraph,
            Request = request,
            SourceId = sourceId,
            SinkId = sinkId,
            EffectiveSinkId = effSink,
            Target = CirclesConverter.TruncateToInt64(targetFlow),
            ReqId = reqId,
            TotalStopwatch = Stopwatch.StartNew(),
            WantDebug = request.DebugShowIntermediateSteps == true,
            DebugStages = request.DebugShowIntermediateSteps == true ? new DebugPipelineStages() : null
        };
    }

    private bool IsBalanceNode(int addr) => AddressIdPool.IsBalanceNode(addr);

    private bool IsPoolNode(int addr)
    {
        if (!AddressIdPool.IsBalanceNode(addr)) return false;
        var str = AddressIdPool.StringOf(addr);
        return str.StartsWith("tpool-");
    }
}
