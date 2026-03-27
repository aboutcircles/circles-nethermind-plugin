using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Int256;

namespace Circles.Pathfinder;

public class V2Pathfinder
{
    private readonly ILogger _logger;
    private readonly Settings _settings;

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
        public int ConsentSafetyNetRejected { get; set; }
        public DebugPipelineStages? DebugStages { get; set; }
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
            _logger.LogWarning("[{ReqId}] Source '{Source}' not in graph snapshot — returning zero flow", reqId, request.Source);
            return null;
        }
        if (!capacityGraph.AvatarNodes.ContainsKey(effSink))
        {
            _logger.LogWarning("[{ReqId}] Sink '{Sink}' not in graph snapshot — returning zero flow", reqId, request.Sink);
            return null;
        }

        _logger.LogInformation("[{ReqId}] Graph: avatars={Avatars}, groups={Groups}, edges={Edges}",
            reqId, capacityGraph.AvatarNodes.Count, capacityGraph.GroupNodes.Count, capacityGraph.Edges.Count);

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
        var beforeCount = ctx.Edges.Count;

        ctx.Edges = _settings.ExcludeConsentedIntermediaries
            ? ctx.Edges
            : ValidateConsentedFlow(ctx.Edges, ctx.Graph);

        ctx.ConsentSafetyNetRejected = beforeCount - ctx.Edges.Count;

        {
            int routerEdges = beforeCount - ctx.Aggregated.Edges.Count;
            _logger.LogInformation(
                "[{ReqId}] Router: +{RouterEdges} edges | Consent: mode={Mode}, pathsDropped={PathsDropped}, safetyNetRejected={Rejected}",
                ctx.ReqId, Math.Max(0, routerEdges),
                _settings.ExcludeConsentedIntermediaries ? "exclude-intermediaries" : "validate-rules",
                ctx.ConsentDroppedPaths, ctx.ConsentSafetyNetRejected);
        }

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
            int routerNode = ctx.Graph.RouterNode ?? -1;
            int collateralEdges = ctx.Edges.Count(e => e.From == routerNode && ctx.Graph.IsGroup(e.To));
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

    /* ======================================================================
     * Stage 11: Build transfer DTOs from flow edges
     * ====================================================================== */

    private void BuildTransferDtos(PipelineContext ctx)
    {
        if (ctx.WantDebug && ctx.Request.QuantizedMode != true)
            ctx.DebugStages!.Sorted = ConvertFlowEdgesToTransferSteps(ctx.Edges);

        ctx.Transfers = new List<TransferPathStep>();

        foreach (var e in ctx.Edges)
        {
            if (e.Flow <= 0)
                continue;

            // Skip sink self-loop edges (display-only, violate flow conservation)
            if (e.From == ctx.SinkId && e.To == ctx.SinkId)
                continue;

            // Resolve From/To — wrapper addresses can leak into the graph as avatar
            // nodes via trust query UNION 2 and appear as flow intermediaries.
            // Hub.sol rejects wrapper addresses as flow vertices (CirclesAvatarMustBeRegistered).
            int resolvedFrom = ctx.Graph.WrapperToAvatar.TryGetValue(e.From, out int fromAvatarId)
                ? fromAvatarId : e.From;
            int resolvedTo = ctx.Graph.WrapperToAvatar.TryGetValue(e.To, out int toAvatarId)
                ? toAvatarId : e.To;

            if (resolvedFrom != e.From || resolvedTo != e.To)
            {
                _logger?.LogWarning(
                    "Resolved wrapper address in flow vertex: From={OrigFrom}→{ResFrom}, To={OrigTo}→{ResTo}",
                    AddressIdPool.StringOf(e.From), AddressIdPool.StringOf(resolvedFrom),
                    AddressIdPool.StringOf(e.To), AddressIdPool.StringOf(resolvedTo));
            }

            // TokenOwner: keep the original token ID (wrapper address for wrapped tokens).
            // Callers use this to determine if unwrapping is needed before Hub.sol submission.
            // For native CRC tokens, e.Token is already the avatar address.
            ctx.Transfers.Add(new TransferPathStep
            {
                From = AddressIdPool.StringOf(resolvedFrom),
                To = AddressIdPool.StringOf(resolvedTo),
                TokenOwner = AddressIdPool.StringOf(e.Token),
                Value = CirclesConverter
                    .BlowUpToUInt256(e.Flow)
                    .ToString(CultureInfo.InvariantCulture)
            });
        }

#if DEBUG
        if (ctx.Transfers.Count > 0)
        {
            var debugState = new Validation.CapacityGraphContractState(ctx.Graph);
            var debugValidation = Validation.HubContractValidator.Validate(
                ctx.Transfers, ctx.Request.Source!, ctx.Request.Sink!, debugState);
            if (!debugValidation.IsValid)
            {
                var errors = debugValidation.Violations
                    .Where(v => v.Severity == "error")
                    .Select(v => $"[{v.Rule}] {v.Message}");
                _logger.LogError("[{ReqId}] HubContractValidator REJECTED output: {Violations}",
                    ctx.ReqId, string.Join("; ", errors));
            }
        }
#endif
    }

    /* ======================================================================
     * Stage 12: Calculate maxFlow and assemble response
     * ====================================================================== */

    private MaxFlowResponse BuildResponse(PipelineContext ctx)
    {
        UInt256 maxFlowWei = 0;
        foreach (var t in ctx.Transfers)
        {
            if (AddressIdPool.IdOf(t.To) == ctx.SinkId)
                maxFlowWei += UInt256.Parse(t.Value);
        }

        ctx.TotalStopwatch.Stop();
        _logger.LogInformation("[{ReqId}] Result: maxFlow={MaxFlow}, steps={Steps}, totalMs={TotalMs}",
            ctx.ReqId, maxFlowWei, ctx.Transfers.Count, ctx.TotalStopwatch.ElapsedMilliseconds);

        return new MaxFlowResponse(
            maxFlowWei.ToString(CultureInfo.InvariantCulture),
            ctx.Transfers,
            ctx.DebugStages)
        {
            ConsentDroppedPaths = ctx.ConsentDroppedPaths,
            ConsentSafetyNetRejected = ctx.ConsentSafetyNetRejected
        };
    }

    /* ------------------------------------------------------------------------
     * Post-process: Insert Router node between Avatar → Group transfers.
     *
     * Mirrors Hub.sol:904-912 _effectPathTransfers() which routes group mints:
     *
     *   if (mintGroups.get(to)) {
     *       _groupMint(
     *           _flowVertices[_coordinates[index + 1]], // sender = "from" of flow edge
     *           to,                                      // receiver = group
     *           _flow.streams[index].circles,
     *           _flow.streams[index].ids[...]
     *       );
     *   }
     *
     * The Router is the intermediary that holds collateral tokens before
     * depositing them to groups. The contract's operateFlowMatrix expects
     * all Avatar→Group transfers to go through the Router.
     *
     * IMPORTANT: This MUST run BEFORE ValidateConsentedFlow because:
     * 1. Without router insertion, edges are Avatar→Group
     * 2. ValidateConsentedFlow would incorrectly check consent for Avatar→Group
     * 3. After insertion, edges become Avatar→Router→Group
     * 4. Router edges are skipped in ValidateConsentedFlow (router has no consent)
     * 5. This matches Hub.sol:723 where _groupMint uses Router as _sender
     * --------------------------------------------------------------------- */
    internal List<FlowEdge> InsertRouterInTransfers(List<FlowEdge> transfers, CapacityGraph capacityGraph)
    {
        if (capacityGraph.RouterNode == null)
            return transfers;

        var result = new List<FlowEdge>();
        int routerId = capacityGraph.RouterNode.Value;

        foreach (var transfer in transfers)
        {
            // If this is Avatar → Group transfer, insert Router in between
            // (Group → Avatar minting transfers are left as-is)
            if (!capacityGraph.IsGroup(transfer.From) &&
                !capacityGraph.IsRouter(transfer.From) &&
                capacityGraph.IsGroup(transfer.To))
            {
                // Split into Avatar → Router → Group
                // Avatar → Router (same token)
                result.Add(new FlowEdge(transfer.From, routerId, transfer.Token, transfer.InitialCapacity)
                {
                    Flow = transfer.Flow,
                    CurrentCapacity = transfer.CurrentCapacity
                });

                // Router → Group (same token)
                result.Add(new FlowEdge(routerId, transfer.To, transfer.Token, transfer.InitialCapacity)
                {
                    Flow = transfer.Flow,
                    CurrentCapacity = transfer.CurrentCapacity
                });
            }
            else
            {
                // Keep as-is (includes Group → Avatar minting transfers)
                result.Add(transfer);
            }
        }

        return result;
    }

    /* ------------------------------------------------------------------------
     * SAFETY NET: Validate consented flow rules on aggregated transfer edges.
     *
     * Mirrors Hub.sol:668-676 isPermittedFlow():
     * - If From NOT consented → standard trust sufficient
     * - If From consented → requires isTrusted(From, To) && advancedUsageFlags[To]
     *
     * Router edges are skipped here — this is the post-insertion counterpart of
     * the IsGroup(to) skip in PathHasConsentViolation. Both exempt group-minting
     * paths from consent checks: PathHasConsentViolation skips Avatar→Group
     * (pre-insertion), this method skips Avatar→Router and Router→Group
     * (post-insertion). Removing either skip breaks the symmetry.
     *
     * Since path-level consent filtering in CollapseBalanceNodes should catch
     * all violations BEFORE aggregation, this method should never filter
     * anything. If it does, log an ERROR — the path-level filter has a gap.
     *
     * Still removes edges for safety (better to produce reduced flow than
     * let the contract revert).
     * --------------------------------------------------------------------- */
    internal List<FlowEdge> ValidateConsentedFlow(List<FlowEdge> edges, CapacityGraph capacityGraph)
    {
        // If no consent data available, pass all edges through
        if (capacityGraph.TrustLookup == null || capacityGraph.ConsentedAvatars.Count == 0)
        {
            return edges;
        }

        var validEdges = new List<FlowEdge>(edges.Count);
        int rejected = 0;

        foreach (var edge in edges)
        {
            // Skip pool nodes - they're not avatars
            if (IsPoolNode(edge.From) || IsPoolNode(edge.To))
            {
                validEdges.Add(edge);
                continue;
            }

            // Catch consented Avatar→Router edges that PathHasConsentViolation should have dropped.
            // Router lacks advancedUsageFlags — consented senders cannot send to it.
            if (capacityGraph.IsRouter(edge.To) && capacityGraph.ConsentedAvatars.Contains(edge.From))
            {
                _logger.LogError(
                    "[ValidateConsentedFlow] SAFETY-NET: consented avatar {From}→Router should have been caught by PathHasConsentViolation",
                    AddressIdPool.StringOf(edge.From)[..10]);
                rejected++;
                continue;
            }

            // Skip remaining router edges — Router is never consented, so standard trust applies.
            // This is the post-insertion counterpart of the IsGroup(to) skip in PathHasConsentViolation.
            if (capacityGraph.IsRouter(edge.From) || capacityGraph.IsRouter(edge.To))
            {
                validEdges.Add(edge);
                continue;
            }

            // If From doesn't have consented flow, standard trust is sufficient
            if (!capacityGraph.ConsentedAvatars.Contains(edge.From))
            {
                validEdges.Add(edge);
                continue;
            }

            // From has consented flow enabled - check additional requirements:
            // 1. From must trust To
            bool fromTrustsTo = capacityGraph.TrustLookup.TryGetValue(edge.From, out var fromTrusts)
                               && fromTrusts.Contains(edge.To);
            if (!fromTrustsTo)
            {
                _logger.LogError("[ValidateConsentedFlow] SAFETY-NET triggered: edge {From}→{To} should have been caught by path-level filter. " +
                    "Consented avatar doesn't trust recipient.",
                    AddressIdPool.StringOf(edge.From)[..10], AddressIdPool.StringOf(edge.To)[..10]);
                rejected++;
                continue;
            }

            // 2. To must also have consented flow enabled
            if (!capacityGraph.ConsentedAvatars.Contains(edge.To))
            {
                _logger.LogError("[ValidateConsentedFlow] SAFETY-NET triggered: edge {From}→{To} should have been caught by path-level filter. " +
                    "Recipient doesn't have consented flow enabled.",
                    AddressIdPool.StringOf(edge.From)[..10], AddressIdPool.StringOf(edge.To)[..10]);
                rejected++;
                continue;
            }

            // All checks passed
            validEdges.Add(edge);
        }

        if (rejected > 0)
        {
            _logger.LogError("[ValidateConsentedFlow] SAFETY-NET removed {Rejected} edges — path-level consent filter has a gap!",
                rejected);
        }

        return validEdges;
    }

    /* ------------------------------------------------------------------------
     * Collapse balance nodes and token pools in a set of paths.
     *
     * IMPORTANT: Consent filtering is done here at the PATH level, BEFORE
     * aggregation. Each solver path is an independent Source→Sink flow.
     * Dropping an entire path preserves flow conservation by construction.
     *
     * Previous approach filtered individual edges AFTER aggregation, which
     * left "holes" — intermediate vertices with unbalanced in/out flows.
     * --------------------------------------------------------------------- */
    private (FlowGraph Graph, int ConsentDroppedPaths) CollapseBalanceNodes(
        List<List<FlowEdge>> pathsWithFlow, CapacityGraph capacityGraph,
        int sourceId, int sinkId)
    {
        var collapsed = new FlowGraph();

        /* ---------------- copy avatars that actually appear ---------------- */
        var avatarSet = new HashSet<int>();

        foreach (var path in pathsWithFlow)
        {
            foreach (var edge in path)
            {
                if (!IsPoolNode(edge.From))
                    avatarSet.Add(edge.From);

                if (!IsPoolNode(edge.To))
                    avatarSet.Add(edge.To);
            }
        }

        foreach (int a in avatarSet)
        {
            collapsed.AddAvatar(a);
        }

        /* ---- collapse per-path, consent-check, drop invalid, aggregate --- */
        var agg = new Dictionary<(int From, int To, int Token), long>();
        int droppedPaths = 0;

        foreach (var path in pathsWithFlow)
        {
            var pathEdges = CollapseSinglePathToEdges(path, capacityGraph);

            if (_settings.ExcludeConsentedIntermediaries)
            {
                // Conservative: exclude paths through consented intermediaries entirely
                if (PathHasConsentedIntermediary(pathEdges, capacityGraph, sourceId, sinkId))
                {
                    droppedPaths++;
                    continue;
                }
            }
            else
            {
                // Normal: apply consent rule validation (isPermittedFlow logic)
                if (PathHasConsentViolation(pathEdges, capacityGraph))
                {
                    droppedPaths++;
                    continue; // Drop entire path — preserves flow conservation
                }
            }

            // Aggregate surviving path edges
            foreach (var (from, to, token, flow) in pathEdges)
            {
                AddToAggregation(agg, from, to, token, flow);
            }
        }

        if (droppedPaths > 0)
        {
            if (droppedPaths == pathsWithFlow.Count)
            {
                _logger.LogWarning(
                    "[CollapseBalanceNodes] ALL {TotalPaths} paths dropped due to consent {Mode} — result will be zero flow. " +
                    "Source or intermediaries may have advancedUsageFlags preventing group minting paths.",
                    pathsWithFlow.Count,
                    _settings.ExcludeConsentedIntermediaries ? "intermediary exclusion" : "violations");
            }
            else
            {
                _logger.LogInformation("[CollapseBalanceNodes] Dropped {DroppedPaths}/{TotalPaths} paths due to consent {Mode}",
                    droppedPaths, pathsWithFlow.Count,
                    _settings.ExcludeConsentedIntermediaries ? "intermediary exclusion" : "violations");
            }
        }

        /* ---------------- materialise collapsed edges ---------------------- */
        foreach (var kvp in agg)
        {
            long flow = kvp.Value;
            if (flow <= 0)
            {
                continue;
            }

            var (from, to, token) = kvp.Key;
            var e = new FlowEdge(from, to, token, long.MaxValue)
            {
                Flow = flow,
                CurrentCapacity = long.MaxValue - flow
            };
            collapsed.Edges.Add(e);
        }

        return (collapsed, droppedPaths);
    }

    /* ------------------------------------------------------------------------
     * Collapse a single path into a list of (From, To, Token, Flow) tuples.
     * Like the old CollapseSinglePath but returns edges instead of aggregating
     * into a shared dictionary — needed for per-path consent checking.
     * --------------------------------------------------------------------- */
    internal List<(int From, int To, int Token, long Flow)> CollapseSinglePathToEdges(
        List<FlowEdge> path,
        CapacityGraph capacityGraph)
    {
        var edges = new List<(int From, int To, int Token, long Flow)>(path.Count);
        int i = 0;

        while (i < path.Count)
        {
            var e = path[i];

            // Case 1: Standard TokenPool collapse (Avatar → TokenPool → Avatar/Group)
            if (IsPoolNode(e.To))
            {
                bool hasNext = (i + 1) < path.Count;
                if (hasNext && path[i + 1].From == e.To)
                {
                    var next = path[i + 1];
                    edges.Add((e.From, next.To, e.Token, Math.Min(e.Flow, next.Flow)));
                    i += 2;
                    continue;
                }
            }

            // Case 2: Group → Avatar (group token minting, keep as-is)
            if (capacityGraph.IsGroup(e.From))
            {
                edges.Add((e.From, e.To, e.Token, e.Flow));
                i += 1;
                continue;
            }

            // Case 3: Any other direct edge (keep as-is)
            if (!IsPoolNode(e.To))
            {
                edges.Add((e.From, e.To, e.Token, e.Flow));
                i += 1;
                continue;
            }

            // Orphaned TokenPool edge
            _logger.LogWarning("[CollapseSinglePathToEdges] Orphaned TokenPool edge at index {Index}: {From}→{To} (token={Token}, flow={Flow})",
                i, AddressIdPool.StringOf(e.From)[..10], AddressIdPool.StringOf(e.To)[..10],
                AddressIdPool.StringOf(e.Token)[..10], e.Flow);
            i += 1;
        }

        return edges;
    }

    /* ------------------------------------------------------------------------
     * Check if a collapsed path has any consent violation.
     *
     * For each edge: if From is consented → check isTrusted(From, To) AND
     * ConsentedAvatars.Contains(To).
     *
     * Skip Avatar→Group edges (where To is a group) — these become
     * Avatar→Router→Group after InsertRouterInTransfers.
     * Group→Avatar (mint) edges stay as-is and must be consent-checked.
     * --------------------------------------------------------------------- */
    internal bool PathHasConsentViolation(
        List<(int From, int To, int Token, long Flow)> collapsedEdges,
        CapacityGraph capacityGraph)
    {
        // No consent data → no violations possible
        if (capacityGraph.TrustLookup == null || capacityGraph.ConsentedAvatars.Count == 0)
            return false;

        foreach (var (from, to, _, _) in collapsedEdges)
        {
            // Skip Avatar→Group edges ONLY when sender is NOT consented.
            // Non-consented: safe — standard trust applies after Router insertion.
            // Consented: DO NOT skip — after Router insertion, Avatar(consented)→Router
            // fails isPermittedFlow because Router lacks advancedUsageFlags.
            if (capacityGraph.IsGroup(to) && !capacityGraph.ConsentedAvatars.Contains(from))
                continue;

            // Skip pool nodes (shouldn't exist after collapse, but safety check)
            if (IsPoolNode(from) || IsPoolNode(to))
                continue;

            // If From doesn't have consented flow, standard trust is sufficient
            if (!capacityGraph.ConsentedAvatars.Contains(from))
                continue;

            // From has consented flow — check requirements:
            // 1. From must trust To
            bool fromTrustsTo = capacityGraph.TrustLookup.TryGetValue(from, out var fromTrusts)
                                && fromTrusts.Contains(to);
            if (!fromTrustsTo)
                return true; // Violation: consented avatar doesn't trust recipient

            // 2. To must also have consented flow enabled
            if (!capacityGraph.ConsentedAvatars.Contains(to))
                return true; // Violation: recipient doesn't have consented flow
        }

        return false;
    }

    /* ------------------------------------------------------------------------
     * Check if a collapsed path routes through any consented avatar as an
     * intermediary (i.e. NOT source or sink). Used when ExcludeConsentedIntermediaries
     * is true — instead of validating consent rules, we simply exclude
     * consented avatars from intermediary positions entirely.
     * --------------------------------------------------------------------- */
    internal bool PathHasConsentedIntermediary(
        List<(int From, int To, int Token, long Flow)> edges,
        CapacityGraph graph, int sourceId, int sinkId)
    {
        if (graph.ConsentedAvatars.Count == 0) return false;

        foreach (var (from, to, _, _) in edges)
        {
            // Skip Avatar→Group edges ONLY when sender is NOT consented.
            // Consented sender → Group becomes Consented → Router after insertion,
            // which fails isPermittedFlow (Router lacks advancedUsageFlags).
            if (graph.IsGroup(to) && !graph.ConsentedAvatars.Contains(from))
                continue;

            if (from != sourceId && from != sinkId && graph.ConsentedAvatars.Contains(from))
                return true;
            if (to != sourceId && to != sinkId && graph.ConsentedAvatars.Contains(to))
                return true;
        }

        return false;
    }

    private void AddToAggregation(
        Dictionary<(int From, int To, int Token), long> agg,
        int from,
        int to,
        int token,
        long flow)
    {
        var key = (from, to, token);
        if (agg.TryGetValue(key, out long existing))
        {
            // Saturating addition to prevent overflow (9.2e18 attoCircles ≈ 9.2 CRC)
            if (existing > long.MaxValue - flow)
            {
                _logger.LogWarning("[AddToAggregation] Saturated flow for edge {From}→{To}: existing={Existing}, flow={Flow}",
                    AddressIdPool.StringOf(from)[..10], AddressIdPool.StringOf(to)[..10], existing, flow);
                agg[key] = long.MaxValue;
            }
            else
            {
                agg[key] = existing + flow;
            }
        }
        else
        {
            agg[key] = flow;
        }
    }

    private bool IsBalanceNode(int addr) => AddressIdPool.IsBalanceNode(addr);

    private bool IsPoolNode(int addr)
    {
        if (!AddressIdPool.IsBalanceNode(addr)) return false;
        var str = AddressIdPool.StringOf(addr);
        return str.StartsWith("tpool-");
    }

    // Count how many (From,To,Token) transfer steps remain after collapsing
    internal static int CountCollapsedTransferSteps(IReadOnlyList<List<SimpleEdge>> paths, CapacityGraph capacityGraph)
    {
        var unique = new HashSet<(int From, int To, int Token)>();

        for (int i = 0; i < paths.Count; i++)
        {
            var triples = CollapsePathToTransfers(paths[i], capacityGraph);
            for (int j = 0; j < triples.Count; j++)
            {
                unique.Add(triples[j]);
            }
        }

        return unique.Count;
    }

    // Greedy pruning: pick paths that give the highest flow per *marginal* step
    private static List<List<SimpleEdge>> PrunePathsByStepLimit(
        IReadOnlyList<List<SimpleEdge>> original,
        int stepCap,
        CapacityGraph capacityGraph)
    {
        // Precompute collapsed triples for each path + path flow.
        var metas = new List<(int Index, long Flow, HashSet<(int F, int T, int K)> Triples)>(original.Count);
        for (int i = 0; i < original.Count; i++)
        {
            var path = original[i];
            long flow = 0;
            if (path.Count > 0)
            {
                flow = path[0].Flow;
            }

            var triples = new HashSet<(int F, int T, int K)>(CollapsePathToTransfers(path, capacityGraph)
                .Select(t => (t.From, t.To, t.Token)));

            metas.Add((i, flow, triples));
        }

        var picked = new bool[original.Count];
        var selectedEdges = new HashSet<(int F, int T, int K)>();
        int stepsLeft = stepCap;

        while (stepsLeft > 0)
        {
            int bestIdx = -1;
            long bestFlow = 0;
            int bestDelta = 0;

            for (int i = 0; i < metas.Count; i++)
            {
                if (picked[i])
                {
                    continue;
                }

                // How many *new* steps would this path introduce?
                int delta = 0;
                foreach (var tr in metas[i].Triples)
                {
                    bool isNew = !selectedEdges.Contains(tr);
                    if (isNew)
                    {
                        delta++;
                    }
                }

                bool fitsBudget = delta <= stepsLeft;
                if (!fitsBudget)
                {
                    continue;
                }

                // Prefer zero-delta (free) additions first.
                if (bestIdx == -1)
                {
                    bestIdx = i;
                    bestFlow = metas[i].Flow;
                    bestDelta = delta;
                    continue;
                }

                if (delta == 0 && bestDelta != 0)
                {
                    bestIdx = i;
                    bestFlow = metas[i].Flow;
                    bestDelta = 0;
                    continue;
                }

                if (delta == 0 && bestDelta == 0)
                {
                    bool betterFlow = metas[i].Flow > bestFlow;
                    if (betterFlow)
                    {
                        bestIdx = i;
                        bestFlow = metas[i].Flow;
                        bestDelta = 0;
                    }

                    continue;
                }

                if (bestDelta == 0)
                {
                    // Current best is free; keep it.
                    continue;
                }

                // Compare flow/delta without floating point: a/b > c/d  <=>  a*d > c*b
                // Use Math.BigMul (returns Int128) to prevent overflow for large flows
                long a = metas[i].Flow;
                long b = delta;
                long c = bestFlow;
                long d = bestDelta;

                var ad = Math.BigMul(a, d);
                var cb = Math.BigMul(c, b);
                bool betterRatio = ad > cb;
                bool tieBreakFewerSteps = ad == cb && delta < bestDelta;
                bool better = betterRatio || tieBreakFewerSteps;

                if (better)
                {
                    bestIdx = i;
                    bestFlow = metas[i].Flow;
                    bestDelta = delta;
                }
            }

            if (bestIdx == -1)
            {
                // Nothing else fits in the remaining budget.
                break;
            }

            // Commit the chosen path
            picked[bestIdx] = true;
            foreach (var tr in metas[bestIdx].Triples)
            {
                bool added = selectedEdges.Add(tr);
                if (added)
                {
                    stepsLeft--;
                    if (stepsLeft == 0)
                    {
                        break;
                    }
                }
            }
        }

        // Preserve original order for stability.
        var pruned = new List<List<SimpleEdge>>();
        for (int i = 0; i < original.Count; i++)
        {
            if (picked[i])
            {
                pruned.Add(original[i]);
            }
        }

        return pruned;
    }

    // Collapse ONE peeled path into transfer triples (FromAvatar, ToAvatar, Token).
    internal static List<(int From, int To, int Token)> CollapsePathToTransfers(
        List<SimpleEdge> path,
        CapacityGraph capacityGraph)
    {
        var triples = new List<(int From, int To, int Token)>(Math.Max(1, path.Count));

        int i = 0;
        while (i < path.Count)
        {
            var e = path[i];

            // Check if this is a pool node
            bool eToIsPool = AddressIdPool.IsBalanceNode(e.To) &&
                            AddressIdPool.StringOf(e.To).StartsWith("tpool-");

            // Standard pool collapse: Avatar → TokenPool → Next
            if (eToIsPool)
            {
                bool hasNext = (i + 1) < path.Count;
                if (hasNext && path[i + 1].From == e.To)
                {
                    var next = path[i + 1];
                    // Collapse to Avatar → Next
                    triples.Add((e.From, next.To, e.Token));
                    i += 2;
                    continue;
                }
            }

            // Direct edges (including Group → Avatar minting)
            triples.Add((e.From, e.To, e.Token));
            i += 1;
        }

        return triples;
    }

    /* ------------------------------------------------------------------------
     * Sort edges to ensure mint dependencies are satisfied.
     *
     * Contract constraint: Groups need to receive ALL collateral BEFORE they
     * can transfer group tokens. The operateFlowMatrix processes edges
     * sequentially, so we must order them correctly.
     *
     * Ordering rules:
     * 1. All Avatar → Router edges (collateral handoff to router)
     * 2. For each group: All Router → Group edges (collateral deposit)
     * 3. For each group: All Group → Avatar edges (group token minting)
     * 4. All other edges (standard avatar-to-avatar transfers)
     *
     * This ensures that when a group's outbound edge is processed, it has
     * already received all the collateral it needs.
     * --------------------------------------------------------------------- */
    internal static List<FlowEdge> SortEdgesForMintDependencies(List<FlowEdge> edges, CapacityGraph capacityGraph)
    {
        if (capacityGraph.RouterNode == null || capacityGraph.GroupNodes.Count == 0)
        {
            // No router or no groups - nothing to sort
            return edges;
        }

        // Categorize edges into buckets
        var avatarToRouter = new List<FlowEdge>();
        var groupEdges = new Dictionary<int, (List<FlowEdge> Inbound, List<FlowEdge> Outbound)>();
        var otherEdges = new List<FlowEdge>();

        foreach (var edge in edges)
        {
            bool fromIsRouter = capacityGraph.IsRouter(edge.From);
            bool toIsRouter = capacityGraph.IsRouter(edge.To);
            bool fromIsGroup = capacityGraph.IsGroup(edge.From);
            bool toIsGroup = capacityGraph.IsGroup(edge.To);

            if (!fromIsRouter && !fromIsGroup && toIsRouter)
            {
                // Avatar → Router (collateral sent to router)
                avatarToRouter.Add(edge);
            }
            else if (fromIsRouter && toIsGroup)
            {
                // Router → Group (collateral deposited to group)
                int groupId = edge.To;
                if (!groupEdges.TryGetValue(groupId, out var lists))
                {
                    lists = (new List<FlowEdge>(), new List<FlowEdge>());
                    groupEdges[groupId] = lists;
                }
                lists.Inbound.Add(edge);
            }
            else if (fromIsGroup && !toIsGroup && !toIsRouter)
            {
                // Group → Avatar (group token minting)
                int groupId = edge.From;
                if (!groupEdges.TryGetValue(groupId, out var lists))
                {
                    lists = (new List<FlowEdge>(), new List<FlowEdge>());
                    groupEdges[groupId] = lists;
                }
                lists.Outbound.Add(edge);
            }
            else
            {
                // All other edges (including direct Avatar → Avatar transfers)
                otherEdges.Add(edge);
            }
        }

        // Build result in dependency order
        var result = new List<FlowEdge>(edges.Count);

        // 1. All Avatar → Router edges first
        result.AddRange(avatarToRouter);

        // 2. For each group: all inbound (Router → Group) before outbound (Group → Avatar)
        foreach (var (groupId, (inbound, outbound)) in groupEdges)
        {
            result.AddRange(inbound);
            result.AddRange(outbound);
        }

        // 3. Other edges last
        result.AddRange(otherEdges);

        return result;
    }

    /* ------------------------------------------------------------------------
     * Validate that edge ordering satisfies mint dependency constraints.
     * Throws InvalidOperationException if ordering is violated.
     *
     * Invariants checked:
     * 1. For each group G, all Router → G edges must appear BEFORE any G → Avatar edge
     * 2. When a Group → Avatar edge is seen, the cumulative inbound flow must be
     *    >= the cumulative outbound flow required so far
     *
     * This validation ensures the contract won't revert with ERC1155InsufficientBalance.
     * --------------------------------------------------------------------- */
    private void ValidateMintEdgeOrdering(PipelineContext ctx)
    {
        var error = ValidateMintEdgeOrdering(ctx.Edges, ctx.Graph);
        if (error != null)
        {
            _logger.LogError("[{ReqId}] {Error}", ctx.ReqId, error);
            ctx.Edges.Clear();
        }
    }

    internal static string? ValidateMintEdgeOrdering(List<FlowEdge> edges, CapacityGraph capacityGraph)
    {
        if (capacityGraph.RouterNode == null || capacityGraph.GroupNodes.Count == 0)
        {
            // No router or no groups - nothing to validate
            return null;
        }

        // Track which groups have had their outbound edge seen
        var groupsWithOutboundSeen = new HashSet<int>();

        // Track cumulative inbound flow per group
        var groupInboundFlow = new Dictionary<int, long>();

        // Track cumulative outbound flow per group
        var groupOutboundFlow = new Dictionary<int, long>();

        for (int i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            bool fromIsRouter = capacityGraph.IsRouter(edge.From);
            bool fromIsGroup = capacityGraph.IsGroup(edge.From);
            bool toIsGroup = capacityGraph.IsGroup(edge.To);

            if (fromIsRouter && toIsGroup)
            {
                // Router → Group (inbound to group)
                int groupId = edge.To;

                // Violation: We've already seen an outbound from this group
                // but we're now seeing more inbound - ordering is wrong
                if (groupsWithOutboundSeen.Contains(groupId))
                {
                    string groupAddr = AddressIdPool.StringOf(groupId);
                    return $"Edge ordering violation: Router → Group edge for group {groupAddr} " +
                        $"appears after Group → Avatar edge at index {i}. " +
                        "All collateral must be deposited before minting.";
                }

                groupInboundFlow.TryGetValue(groupId, out long current);
                groupInboundFlow[groupId] = current + edge.Flow;
            }
            else if (fromIsGroup && !capacityGraph.IsRouter(edge.To) && !capacityGraph.IsGroup(edge.To))
            {
                // Group → Avatar (outbound from group - minting)
                int groupId = edge.From;
                groupsWithOutboundSeen.Add(groupId);

                groupOutboundFlow.TryGetValue(groupId, out long currentOutbound);
                groupOutboundFlow[groupId] = currentOutbound + edge.Flow;

                // Check flow conservation: cumulative inbound >= cumulative outbound so far
                groupInboundFlow.TryGetValue(groupId, out long inbound);
                if (inbound < groupOutboundFlow[groupId])
                {
                    string groupAddr = AddressIdPool.StringOf(groupId);
                    return $"Flow violation: Group {groupAddr} has insufficient collateral at edge index {i}. " +
                        $"Cumulative inbound: {inbound}, cumulative outbound required: {groupOutboundFlow[groupId]}.";
                }
            }
        }

        return null;
    }

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

        // BFS backwards from sink
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

        while (queue.Count > 0)
        {
            int vertex = queue.Dequeue();
            if (!visited.Add(vertex)) continue;
            if (vertex == sourceId) continue; // Source net flow is allowed to change

            if (!incomingEdges.TryGetValue(vertex, out var inIndices) || inIndices.Count == 0)
                continue; // No incoming edges — this is a source vertex

            // Determine if this is a token-converting vertex (e.g., group minting).
            // At groups: collateral tokens come in, group tokens go out — different types.
            // At normal intermediaries: same token types flow through.
            var inTokenTypes = new HashSet<int>();
            foreach (int idx in inIndices)
                inTokenTypes.Add(edges[idx].Token);

            var outTokenTypes = new HashSet<int>();
            long totalOut = 0;
            if (outgoingEdges.TryGetValue(vertex, out var outIndices))
            {
                foreach (int idx in outIndices)
                {
                    outTokenTypes.Add(edges[idx].Token);
                    totalOut += edges[idx].Flow;
                }
            }

            long totalIn = 0;
            foreach (int idx in inIndices)
                totalIn += edges[idx].Flow;

            if (totalIn <= totalOut) continue; // Already balanced

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
                    queue.Enqueue(e.From);
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
                        queue.Enqueue(e.From);
                    }
                }
            }
        }
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

    /* ------------------------------------------------------------------------
     * DEBUG HELPERS: Convert internal edge representations to TransferPathStep
     * for debug output showing all transformation stages.
     * --------------------------------------------------------------------- */

    /// <summary>
    /// Convert raw paths (List of SimpleEdge lists) to TransferPathStep list.
    /// Flattens all paths and shows edges with token pools visible.
    /// </summary>
    private static List<TransferPathStep> ConvertSimplePathsToTransferSteps(IReadOnlyList<List<SimpleEdge>> paths)
    {
        var result = new List<TransferPathStep>();

        foreach (var path in paths)
        {
            foreach (var edge in path)
            {
                if (edge.Flow <= 0)
                    continue;

                result.Add(new TransferPathStep
                {
                    From = AddressIdPool.StringOf(edge.From),
                    To = AddressIdPool.StringOf(edge.To),
                    TokenOwner = AddressIdPool.StringOf(edge.Token),
                    Value = CirclesConverter
                        .BlowUpToUInt256(edge.Flow)
                        .ToString(CultureInfo.InvariantCulture)
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Convert FlowEdge list to TransferPathStep list.
    /// </summary>
    private static List<TransferPathStep> ConvertFlowEdgesToTransferSteps(IReadOnlyList<FlowEdge> edges)
    {
        var result = new List<TransferPathStep>(edges.Count);

        foreach (var edge in edges)
        {
            if (edge.Flow <= 0)
                continue;

            result.Add(new TransferPathStep
            {
                From = AddressIdPool.StringOf(edge.From),
                To = AddressIdPool.StringOf(edge.To),
                TokenOwner = AddressIdPool.StringOf(edge.Token),
                Value = CirclesConverter
                    .BlowUpToUInt256(edge.Flow)
                    .ToString(CultureInfo.InvariantCulture)
            });
        }

        return result;
    }
}
