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

    public V2Pathfinder(ILogger<V2Pathfinder>? logger = null)
    {
        _logger = logger ?? NullLogger<V2Pathfinder>.Instance;
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

        bool sourceMissing = !capacityGraph.AvatarNodes.ContainsKey(sourceId);
        if (sourceMissing)
        {
            throw new ArgumentException($"Source '{flowRequest.Source}' isn't in the graph snapshot.");
        }

        bool sinkMissing = !capacityGraph.AvatarNodes.ContainsKey(effSink);
        if (sinkMissing)
        {
            throw new ArgumentException($"Sink '{flowRequest.Sink}' isn't in the graph snapshot.");
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
        /* --------------------------------------------------------------------
         * 1. Resolve ids and basic guards
         * ------------------------------------------------------------------ */
        int sinkId = AddressIdPool.IdOf(request.Sink ?? throw new ArgumentNullException(nameof(request.Sink)));
        int effSink = capacityGraph.VirtualSinkAddress ?? sinkId;
        int sourceId = AddressIdPool.IdOf(request.Source ?? throw new ArgumentNullException(nameof(request.Source)));

        bool srcMissing = !capacityGraph.AvatarNodes.ContainsKey(sourceId);
        bool dstMissing = !capacityGraph.AvatarNodes.ContainsKey(effSink);
        if (srcMissing)
            throw new ArgumentException($"Source '{request.Source}' isn't in the graph snapshot.");
        if (dstMissing)
            throw new ArgumentException($"Sink '{request.Sink}' isn't in the graph snapshot.");

        /* --------------------------------------------------------------------
         * 2. Run max-flow and peel paths
         * ------------------------------------------------------------------ */
        var reqId = Guid.NewGuid().ToString("N")[..8];
        var totalSw = Stopwatch.StartNew();

        _logger.LogInformation("[{ReqId}] Graph: avatars={Avatars}, groups={Groups}, edges={Edges}",
            reqId, capacityGraph.AvatarNodes.Count, capacityGraph.GroupNodes.Count, capacityGraph.Edges.Count);

        long tgt = CirclesConverter.TruncateToInt64(targetFlow);
        var solveSw = Stopwatch.StartNew();
        var solved = MaxFlowSolver.Solve(capacityGraph.Edges, sourceId, effSink, tgt);
        solveSw.Stop();
        var simplePaths = PathUtils.ExtractFlowPaths(solved, sourceId, effSink);

        {
            long solvedFlow = solved.Where(e => e.From == sourceId && e.Flow > 0).Sum(e => e.Flow);
            _logger.LogInformation("[{ReqId}] MaxFlow: flow={Flow}, edgesWithFlow={EdgesWithFlow}, paths={Paths}, solveMs={SolveMs}",
                reqId, solvedFlow, solved.Count(e => e.Flow > 0), simplePaths.Count, solveSw.ElapsedMilliseconds);
        }

        /* --------------------------------------------------------------------
         * 2a. NOTE: Quantization for invitation module moved to step 6d (after aggregation)
         *     Old per-path quantization filtered paths individually, missing cases where
         *     multiple paths deliver the same token type that together form valid quanta.
         * ------------------------------------------------------------------ */

        /* --------------------------------------------------------------------
         * 2b. CAPTURE: Stage 1 - Raw paths from solver (before pruning/collapsing)
         *     Shows Avatar → TokenPool → Avatar paths with token pools visible.
         * ------------------------------------------------------------------ */
        bool wantDebug = request.DebugShowIntermediateSteps == true;
        DebugPipelineStages? debugStages = wantDebug ? new DebugPipelineStages() : null;

        if (wantDebug)
        {
            debugStages!.RawPaths = ConvertSimplePathsToTransferSteps(simplePaths);
        }

        /* --------------------------------------------------------------------
         * 2c. Optional pruning to fit a transfer-step budget
         *      We keep the biggest-flow paths first and count "steps" after
         *      collapsing balance nodes (avatar→avatar per token).
         * ------------------------------------------------------------------ */
        bool hasStepCap = request.MaxTransfers.HasValue && request.MaxTransfers.Value > 0;
        if (hasStepCap)
        {
            int stepCap = request.MaxTransfers!.Value;
            int currentSteps = CountCollapsedTransferSteps(simplePaths, capacityGraph);

            bool needsPrune = currentSteps > stepCap;
            if (needsPrune)
            {
                simplePaths = PrunePathsByStepLimit(simplePaths, stepCap, capacityGraph);
            }
        }

        /* --------------------------------------------------------------------
         * 3. Convert SimpleEdge → FlowEdge
         * ------------------------------------------------------------------ */
        var flowPaths = new List<List<FlowEdge>>(simplePaths.Count);

        foreach (var path in simplePaths)
        {
            var list = new List<FlowEdge>(path.Count);
            foreach (var e in path)
            {
                var fe = new FlowEdge(e.From, e.To, e.Token, e.Capacity)
                {
                    Flow = e.Flow,
                    CurrentCapacity = e.Capacity
                };
                list.Add(fe);
            }

            flowPaths.Add(list);
        }

        /* --------------------------------------------------------------------
         * 4. Replace virtual-sink ids (if any) with the real sink id
         * ------------------------------------------------------------------ */
        if (capacityGraph.VirtualSinkAddress != null)
        {
            int vs = capacityGraph.VirtualSinkAddress.Value;

            var replaced = new List<List<FlowEdge>>(flowPaths.Count);
            foreach (var path in flowPaths)
            {
                var fixedPath = new List<FlowEdge>(path.Count);
                foreach (var fe in path)
                {
                    int from = fe.From == vs ? sinkId : fe.From;
                    int to = fe.To == vs ? sinkId : fe.To;

                    var copy = new FlowEdge(from, to, fe.Token, fe.InitialCapacity)
                    {
                        Flow = fe.Flow,
                        CurrentCapacity = fe.CurrentCapacity
                    };
                    fixedPath.Add(copy);
                }

                replaced.Add(fixedPath);
            }

            flowPaths = replaced;
        }

        /* --------------------------------------------------------------------
         * 5. Collapse balance nodes + aggregate identical edges
         * ------------------------------------------------------------------ */
        var collapsed = CollapseBalanceNodes(flowPaths, capacityGraph);
        var aggregated = collapsed.AggregateIdenticalEdges();

        {
            int beforeCollapse = flowPaths.Sum(p => p.Count);
            _logger.LogInformation("[{ReqId}] Collapsed: before={Before}, after={After}, droppedZero={DroppedZero}",
                reqId, beforeCollapse, aggregated.Edges.Count, aggregated.Edges.Count(e => e.Flow <= 0));
        }

        /* --------------------------------------------------------------------
         * 5a. CAPTURE: Stage 2 - Collapsed edges (token pools removed)
         * ------------------------------------------------------------------ */
        if (wantDebug)
        {
            debugStages!.Collapsed = ConvertFlowEdgesToTransferSteps(aggregated.Edges);
        }

        /* --------------------------------------------------------------------
         * 5b. Insert Router between Avatar → Group transfers.
         *     This MUST happen BEFORE ValidateConsentedFlow so that router edges
         *     exist when validation runs - allowing the router-skip logic to work.
         *     Without this order, consented flow avatars sending to groups would
         *     have their Avatar→Group edge incorrectly filtered because the router
         *     edges (Avatar→Router, Router→Group) don't exist yet.
         * ------------------------------------------------------------------ */
        var processedEdges = InsertRouterInTransfers(aggregated.Edges, capacityGraph);

        /* --------------------------------------------------------------------
         * 5c. Validate consented flow - filter edges that violate consent rules.
         *     Router edges are skipped by this validation (lines 332-337).
         * ------------------------------------------------------------------ */
        var validatedEdges = ValidateConsentedFlow(processedEdges, capacityGraph);

        {
            int routerEdges = processedEdges.Count - aggregated.Edges.Count;
            int consentRejected = processedEdges.Count - validatedEdges.Count;
            _logger.LogInformation("[{ReqId}] Router: +{RouterEdges} edges | Consent: passed={Passed}, rejected={Rejected}",
                reqId, Math.Max(0, routerEdges), validatedEdges.Count, consentRejected);
        }

        /* --------------------------------------------------------------------
         * 5d. CAPTURE: Stage 3 - Router inserted (Avatar→Group → Avatar→Router→Group)
         * ------------------------------------------------------------------ */
        if (wantDebug)
        {
            debugStages!.RouterInserted = ConvertFlowEdgesToTransferSteps(validatedEdges);
        }

        /* --------------------------------------------------------------------
         * 6. Sort edges to ensure mint dependencies are satisfied.
         *    All collateral edges (Router→Group) must precede the group's
         *    outbound mint edge (Group→Avatar) for contract execution to succeed.
         * ------------------------------------------------------------------ */
        var sortedEdges = SortEdgesForMintDependencies(validatedEdges, capacityGraph);

        /* --------------------------------------------------------------------
         * 6a. Validate the edge ordering - throw if mint dependencies violated
         * ------------------------------------------------------------------ */
        ValidateMintEdgeOrdering(sortedEdges, capacityGraph);

        {
            int groupMints = sortedEdges.Count(e => capacityGraph.IsGroup(e.From));
            int routerNode = capacityGraph.RouterNode ?? -1;
            int collateralEdges = sortedEdges.Count(e => e.From == routerNode && capacityGraph.IsGroup(e.To));
            _logger.LogInformation("[{ReqId}] MintSort: groups={Groups}, collateral={Collateral}, total={Total}",
                reqId, groupMints, collateralEdges, sortedEdges.Count);
        }

        /* --------------------------------------------------------------------
         * 6b. Apply quantization if requested - aggregate sink transfers by token
         *     and round down to 96 CRC multiples. This allows multiple small
         *     transfers of the same token to combine into valid quanta.
         * ------------------------------------------------------------------ */
        if (request.QuantizedMode == true)
        {
            const long InvitationQuanta = 96_000_000L;
            int preQuantCount = sortedEdges.Count;
            sortedEdges = QuantizeSinkBoundEdgesByToken(sortedEdges, sinkId, InvitationQuanta, tgt);

            // Propagate quantization reduction backwards through the edge graph
            // to maintain flow conservation at intermediate vertices (Router, Group)
            PropagateQuantizationBackwards(sortedEdges, sinkId, sourceId);

            // Safety validation - ensure quantization produced valid results
            ValidateQuantizedSinkTransfers(sortedEdges, sinkId, InvitationQuanta);

            // Add self-loop aggregation: Sink → Sink edges showing total per token type
            // This provides a summary of what tokens the sink receives in the quantized flow
            sortedEdges = AddSinkSelfLoopAggregation(sortedEdges, sinkId);

            _logger.LogInformation("[{ReqId}] Quantized: before={Before}, after={After}, quanta={Quanta}",
                reqId, preQuantCount, sortedEdges.Count, InvitationQuanta);
        }

        /* --------------------------------------------------------------------
         * 6c. CAPTURE: Stage 4 - Sorted edges (final execution order)
         *
         *    NOTE: Debug output includes sink self-loop aggregation edges
         *    (Sink→Sink) for visibility. The actual transfer list (step 7)
         *    filters these out since they are display-only and violate
         *    Hub.sol's flow conservation requirement.
         * ------------------------------------------------------------------ */
        if (wantDebug)
        {
            debugStages!.Sorted = ConvertFlowEdgesToTransferSteps(sortedEdges);
        }

        /* --------------------------------------------------------------------
         * 7. Build DTOs
         * ------------------------------------------------------------------ */
        var transfer = new List<TransferPathStep>();

        foreach (var e in sortedEdges)
        {
            if (e.Flow <= 0)
            {
                continue;
            }

            // Skip sink self-loop edges added by AddSinkSelfLoopAggregation()
            // These are display-only aggregation edges for quantized mode responses
            // and should NOT be sent to the Hub contract (they violate flow conservation)
            if (e.From == sinkId && e.To == sinkId)
            {
                continue;
            }

            var step = new TransferPathStep
            {
                From = AddressIdPool.StringOf(e.From),
                To = AddressIdPool.StringOf(e.To),
                TokenOwner = AddressIdPool.StringOf(e.Token),
                Value = CirclesConverter
                    .BlowUpToUInt256(e.Flow)
                    .ToString(CultureInfo.InvariantCulture)
            };
            transfer.Add(step);
        }

        /* --------------------------------------------------------------------
         * 7a. DEBUG: Validate output against Hub.sol rules
         * ------------------------------------------------------------------ */
#if DEBUG
        if (transfer.Count > 0)
        {
            var debugState = new Validation.CapacityGraphContractState(capacityGraph);
            var debugValidation = Validation.HubContractValidator.Validate(
                transfer, request.Source!, request.Sink!, debugState);
            if (!debugValidation.IsValid)
            {
                var errors = debugValidation.Violations
                    .Where(v => v.Severity == "error")
                    .Select(v => $"[{v.Rule}] {v.Message}");
                _logger.LogError("[{ReqId}] HubContractValidator REJECTED output: {Violations}",
                    reqId, string.Join("; ", errors));
            }
        }
#endif

        /* --------------------------------------------------------------------
         * 8. Calculate maxFlow by summing transfers TO the sink.
         *    We sum sink-bound transfers (not source-outbound) because after
         *    path collapsing, multi-hop paths may no longer have the original
         *    source in the 'from' field. The total reaching the sink correctly
         *    represents the achievable flow.
         *
         *    NOTE: Sink self-loop aggregation edges (Sink→Sink) added by
         *    AddSinkSelfLoopAggregation() are already filtered out in step 7
         *    when building the transfer list, so they won't appear here.
         * ------------------------------------------------------------------ */
        UInt256 maxFlowWei = 0;
        foreach (var t in transfer)
        {
            var toId = AddressIdPool.IdOf(t.To);
            bool toIsSink = toId == sinkId;
            if (toIsSink)
                maxFlowWei += UInt256.Parse(t.Value);
        }

        totalSw.Stop();
        _logger.LogInformation("[{ReqId}] Result: maxFlow={MaxFlow}, steps={Steps}, totalMs={TotalMs}",
            reqId, maxFlowWei, transfer.Count, totalSw.ElapsedMilliseconds);

        return new MaxFlowResponse(
            maxFlowWei.ToString(CultureInfo.InvariantCulture),
            transfer,
            debugStages);
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
     * Router edges skipped (router has no consent, Hub.sol:723 uses Router as sender).
     *
     * Since path-level consent filtering in CollapseBalanceNodes should catch
     * all violations BEFORE aggregation, this method should never filter
     * anything. If it does, log an ERROR — the path-level filter has a gap.
     *
     * Still removes edges for safety (better to produce reduced flow than
     * let the contract revert).
     * --------------------------------------------------------------------- */
    private List<FlowEdge> ValidateConsentedFlow(List<FlowEdge> edges, CapacityGraph capacityGraph)
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

            // Skip router edges
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
    private FlowGraph CollapseBalanceNodes(List<List<FlowEdge>> pathsWithFlow, CapacityGraph capacityGraph)
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

            if (PathHasConsentViolation(pathEdges, capacityGraph))
            {
                droppedPaths++;
                continue; // Drop entire path — preserves flow conservation
            }

            // Aggregate surviving path edges
            foreach (var (from, to, token, flow) in pathEdges)
            {
                AddToAggregation(agg, from, to, token, flow);
            }
        }

        if (droppedPaths > 0)
        {
            _logger.LogInformation("[CollapseBalanceNodes] Dropped {DroppedPaths}/{TotalPaths} paths due to consent violations",
                droppedPaths, pathsWithFlow.Count);
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

        return collapsed;
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
     * Skip edges where From or To is a group — these become router edges
     * after InsertRouterInTransfers, and the router bypasses consent checks.
     * The consented-flow-router-006 scenario depends on this.
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
            // Skip group edges — they become router edges later (router bypasses consent)
            if (capacityGraph.IsGroup(from) || capacityGraph.IsGroup(to))
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
    internal static void ValidateMintEdgeOrdering(List<FlowEdge> edges, CapacityGraph capacityGraph)
    {
        if (capacityGraph.RouterNode == null || capacityGraph.GroupNodes.Count == 0)
        {
            // No router or no groups - nothing to validate
            return;
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
                    throw new InvalidOperationException(
                        $"Edge ordering violation: Router → Group edge for group {groupAddr} " +
                        $"appears after Group → Avatar edge at index {i}. " +
                        "All collateral must be deposited before minting.");
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
                    throw new InvalidOperationException(
                        $"Flow violation: Group {groupAddr} has insufficient collateral at edge index {i}. " +
                        $"Cumulative inbound: {inbound}, cumulative outbound required: {groupOutboundFlow[groupId]}. " +
                        "Ensure all Router → Group edges precede Group → Avatar edges.");
                }
            }
        }
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

            // Compute current total outflow
            long totalOut = 0;
            if (outgoingEdges.TryGetValue(vertex, out var outIndices))
            {
                foreach (int idx in outIndices)
                    totalOut += edges[idx].Flow;
            }

            // Compute current total inflow
            long totalIn = 0;
            if (!incomingEdges.TryGetValue(vertex, out var inIndices) || inIndices.Count == 0)
                continue; // No incoming edges — this is a source vertex

            foreach (int idx in inIndices)
                totalIn += edges[idx].Flow;

            if (totalIn <= totalOut) continue; // Already balanced or underflow (shouldn't happen)

            // Scale incoming edges to match outflow
            long allocated = 0;
            for (int j = 0; j < inIndices.Count; j++)
            {
                int idx = inIndices[j];
                var e = edges[idx];
                long newFlow;

                if (j == inIndices.Count - 1)
                {
                    // Last edge gets remainder for exact conservation
                    newFlow = totalOut - allocated;
                }
                else
                {
                    // Proportional scaling
                    newFlow = (e.Flow * totalOut) / totalIn;
                }

                if (newFlow < 0) newFlow = 0;
                e.Flow = newFlow;
                allocated += newFlow;

                // Enqueue the predecessor vertex
                queue.Enqueue(e.From);
            }
        }
    }

    /* ------------------------------------------------------------------------
     * Validate that the TOTAL flow per token type to sink is a multiple of quantaSize.
     * Individual edges may have non-quantized flows, but their sum per token must be.
     * Used as a safety check after quantization to ensure correctness.
     * --------------------------------------------------------------------- */
    private static void ValidateQuantizedSinkTransfers(List<FlowEdge> edges, int sinkId, long quantaSize)
    {
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
                throw new InvalidOperationException(
                    $"Quantization violation: Total flow of token {tokenAddr} to sink is {totalFlow}, " +
                    $"which is not a multiple of {quantaSize} (96 CRC). " +
                    "Total per token type must be exact 96 CRC multiples in quantized mode.");
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