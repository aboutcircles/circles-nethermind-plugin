using System.Diagnostics;
using System.Text.Json;
using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host.Canary;
using Circles.Pathfinder.Host.State;
using Circles.Pathfinder.Validation;
using Nethermind.Int256;
using static Circles.Pathfinder.Tracing;

namespace Circles.Pathfinder.Host;

/// <summary>
/// Shared handler logic for GET /findMaxFlow, GET /findPath, and POST /findPath.
/// Eliminates ~80% code duplication across the three endpoints:
/// semaphore, graph readiness, pool rent, solver timeout, metrics, exception handling.
/// </summary>
internal sealed class FindPathHandler(
    Settings settings,
    SemaphoreSlim semaphore,
    ILogger<FindPathHandler> log,
    IHttpContextAccessor httpContextAccessor,
    HistoricalGraphCache historicalGraphCache,
    SimulationCanaryService? simulationCanary = null)
{
    private const int MaxArrayEntries = 1000;

    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Validate Ethereum address format. Returns an error result if invalid, null if OK.
    /// </summary>
    internal static IResult? ValidateAddresses(params (string? addr, string name)[] pairs)
    {
        foreach (var (addr, name) in pairs)
        {
            if (!string.IsNullOrWhiteSpace(addr) && !GraphFactory.IsValidEthereumAddress(addr.Trim().ToLowerInvariant()))
                return Results.BadRequest($"Invalid Ethereum address for '{name}': {addr}");
        }
        return null;
    }

    /// <summary>
    /// Parse simulatedBalances from a JSON query string parameter.
    /// Returns null list on empty input, or an error result on parse failure / size overflow.
    /// </summary>
    internal static (List<SimulatedBalance>? sim, IResult? error) ParseSimulatedBalances(string? json,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, null);

        try
        {
            var sim = JsonSerializer.Deserialize<List<SimulatedBalance>>(json, CaseInsensitiveJson);
            if (sim != null && sim.Count > MaxArrayEntries)
                return (null, Results.BadRequest($"simulatedBalances exceeds maximum of {MaxArrayEntries} entries."));
            return (sim, null);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed to parse simulatedBalances query parameter ({Length} chars)", json.Length);
            return (null, Results.BadRequest("simulatedBalances must be a JSON array of objects."));
        }
    }

    /// <summary>
    /// Validate POST body array sizes.
    /// </summary>
    internal static IResult? ValidateArraySizes(FlowRequest request)
    {
        if (request.SimulatedBalances?.Count > MaxArrayEntries)
            return Results.BadRequest($"simulatedBalances exceeds maximum of {MaxArrayEntries} entries.");
        if (request.SimulatedTrusts?.Count > MaxArrayEntries)
            return Results.BadRequest($"simulatedTrusts exceeds maximum of {MaxArrayEntries} entries.");
        if (request.SimulatedConsentedAvatars?.Count > MaxArrayEntries)
            return Results.BadRequest($"simulatedConsentedAvatars exceeds maximum of {MaxArrayEntries} entries.");
        return null;
    }

    /// <summary>
    /// Build a FlowRequest from GET query parameters.
    /// </summary>
    internal FlowRequest BuildRequest(
        string from, string to, string amount,
        string[]? fromTokens, string[]? toTokens,
        string[]? excludedFromTokens, string[]? excludedToTokens,
        bool? withWrap, List<SimulatedBalance>? sim,
        string[]? simulatedConsentedAvatars,
        int? maxTransfers = null, bool? quantizedMode = null,
        bool? debugShowIntermediateSteps = null)
    {
        // Normalize and sanitize addresses once so all downstream logging is safe
        // (prevents log forging via embedded \r\n in user-supplied addresses).
        var normalizedFrom = from.ToLowerInvariant().Replace("\r", string.Empty).Replace("\n", string.Empty);
        var normalizedTo = to.ToLowerInvariant().Replace("\r", string.Empty).Replace("\n", string.Empty);

        return new FlowRequest
        {
            Source = normalizedFrom,
            Sink = normalizedTo,
            TargetFlow = amount,
            FromTokens = fromTokens?.ToList(),
            ToTokens = toTokens?.ToList(),
            ExcludedFromTokens = excludedFromTokens?.ToList(),
            ExcludedToTokens = excludedToTokens?.ToList(),
            WithWrap = withWrap,
            SimulatedBalances = sim,
            SimulatedConsentedAvatars = settings.ExcludeConsentedIntermediaries ? null : simulatedConsentedAvatars?.ToList(),
            MaxTransfers = maxTransfers,
            QuantizedMode = quantizedMode,
            DebugShowIntermediateSteps = debugShowIntermediateSteps
        };
    }

    /// <summary>
    /// Strip CR/LF from user-supplied values before logging (prevents log forging).
    /// </summary>
    private static string? SanitizeForLog(string? value) =>
        value?.Replace("\r", string.Empty).Replace("\n", string.Empty);

    /// <summary>
    /// Comma-joins a token-filter list for the structured request log (CR/LF stripped per element),
    /// or "" when null/empty. These filters materially change which path the solver builds (e.g.
    /// group-targeted payments), so they must be logged for the canary replay to reconstruct the
    /// exact request shape. Comma-separated so the value stays a single space-delimited token.
    /// </summary>
    private static string SanitizeTokenList(IReadOnlyList<string>? tokens) =>
        tokens is not { Count: > 0 }
            ? string.Empty
            : string.Join(",", tokens.Select(t => SanitizeForLog(t)));

    /// <summary>
    /// Count of a simulation-override list for the structured request log (0 when null/empty).
    /// A non-zero count means the request injected what-if balances/trusts/consent the solver used
    /// but the log can't carry verbatim, so the canary replay can't faithfully reconstruct it — the
    /// replay classifies such requests as skipped rather than as a (false) maxFlow divergence.
    /// </summary>
    private static int SimCount<T>(IReadOnlyCollection<T>? list) => list?.Count ?? 0;

    /// <summary>
    /// Rules a simulated request can legitimately trip because the injected what-if entity isn't
    /// real on-chain: an unregistered hypothetical avatar (AvatarRegistration), an unregistered
    /// hypothetical group (GroupRegistration), or a flow permitted only via a simulated trust/consent
    /// (IsPermittedFlow). ONLY these are eligible for simulated="true" (alert-excluded). Every other
    /// rule — FlowConservation, CollateralBeforeMint, VertexOrdering, NoZeroFlow, AddressFormat,
    /// TokenIdValidity, ScoreGroupMintLimitsHonored, ValidatorException — validates the solver's own
    /// output and is a real pathfinder bug regardless of injected state, so it always pages.
    /// </summary>
    private static readonly HashSet<string> SimulatedExcusableRules =
        new(StringComparer.Ordinal) { "AvatarRegistration", "GroupRegistration", "IsPermittedFlow" };

    /// <summary>
    /// Records the path-audit violation + blocked counters for a rejected response, tagging each
    /// series with <c>simulated</c> ("true"/"false"). The exclusion is gated PER-RULE, not per
    /// request: a violation is tagged simulated="true" (alert-excluded) only when the request
    /// injected what-if state AND the rule is in <see cref="SimulatedExcusableRules"/>. This way a
    /// frontend what-if preview (e.g. AvatarRegistration on a hypothetical source) stops paging,
    /// but a genuine solver-integrity bug riding on a simulated request (e.g. FlowConservation)
    /// still pages. The aggregate "any" series is excused only when every violated rule is excusable
    /// and there is no validator exception — otherwise a real bug could hide behind a true-tagged
    /// "any". Mirrors the canary's category=simulation exclusion. Violations and blocked increment
    /// 1:1 (the safety net always replaces a violating response with the empty path).
    /// </summary>
    internal static void RecordPathAuditViolation(MaxFlowResponse mfr, bool simulated)
    {
        void Bump(string rule)
        {
            var sim = simulated && SimulatedExcusableRules.Contains(rule) ? "true" : "false";
            FindPathMetrics.PathAuditViolationsTotal.WithLabels(rule, sim).Inc();
            FindPathMetrics.PathAuditBlockedTotal.WithLabels(rule, sim).Inc();
        }

        // "any" is excused only when the whole rejection is attributable to simulated input —
        // every violated rule excusable and no validator exception. A mixed rejection (one
        // excusable rule + one integrity rule) keeps "any" simulated="false" so it still pages.
        bool allExcusable = simulated
                            && !mfr.ValidatorException
                            && mfr.ValidationViolationRules is { Count: > 0 }
                            && mfr.ValidationViolationRules.All(SimulatedExcusableRules.Contains);
        var anySim = allExcusable ? "true" : "false";
        FindPathMetrics.PathAuditViolationsTotal.WithLabels("any", anySim).Inc();
        FindPathMetrics.PathAuditBlockedTotal.WithLabels("any", anySim).Inc();

        if (mfr.ValidationViolationRules != null)
        {
            foreach (var rule in mfr.ValidationViolationRules)
                Bump(rule);
        }

        if (mfr.ValidatorException)
            Bump("ValidatorException"); // never excusable → always simulated="false"
    }

    /// <summary>
    /// Extracts X-Max-Block-Number header from the current HTTP request, if present.
    /// When set, the pathfinder uses a historical graph at that block instead of the live graph.
    /// </summary>
    private long? GetMaxBlockNumberFromHeader()
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext == null) return null;

        if (httpContext.Request.Headers.TryGetValue("X-Max-Block-Number", out var headerValue)
            && long.TryParse(headerValue.FirstOrDefault(), out var blockNumber))
        {
            return blockNumber;
        }

        return null;
    }

    internal async Task<IResult> ExecuteWithGuard(
        string route,
        FlowRequest request,
        NetworkState state,
        CapacityGraphPool pool,
        Func<CapacityGraph, object> solve)
    {
        // Check for historical block header — if present, use block-filtered graph
        var maxBlockNumber = GetMaxBlockNumberFromHeader();
        if (maxBlockNumber.HasValue)
        {
            return await ExecuteHistorical(route, request, maxBlockNumber.Value, solve);
        }

        if (!semaphore.Wait(0))
        {
            FindPathMetrics.RejectedRequestsCounter.Inc();
            log.LogWarning("Concurrency limit hit — request rejected");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var sw = Stopwatch.StartNew();
        var graphBlock = state.LastKnownBlockNumber;

        // Sanitize once for all log branches (prevents log forging via CR/LF in user input)
        var safeSource = SanitizeForLog(request.Source);
        var safeSink = SanitizeForLog(request.Sink);
        var safeTargetFlow = SanitizeForLog(request.TargetFlow);

        FindPathMetrics.InFlightRequestsGauge.Inc();
        try
        {
            var balanceGraph = state.BalanceGraph;
            var trustGraph = state.AccountTrusts;

            if (balanceGraph is null)
            {
                // Transient warmup (post-restart / reorg / upstream not ready), not a
                // client error — 503 so load balancers drain & clients retry.
                FindPathMetrics.SolverStatusTotal.WithLabels("not_ready").Inc();
                log.LogWarning("Graphs not ready");
                return Results.Text("Graphs are not loaded yet.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!pool.HasCurrentSnapshot)
            {
                // Mirrors the capacity-graph leg of /ready (GraphReadinessHealthCheck
                // also checks AccountTrusts; here we only need pool state). Without
                // this gate, pool.Rent throws InvalidOperationException → generic
                // catch → 500 for a recoverable warmup state.
                FindPathMetrics.SolverStatusTotal.WithLabels("not_ready").Inc();
                log.LogWarning("Capacity graph snapshot not ready — returning 503 (warmup)");
                return Results.Text("Graphs are not loaded yet.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            using var h = await pool.Rent(request, balanceGraph, trustGraph);
            graphBlock = h.Graph.Block; // use the rented graph's actual block

            var solverTimeout = TimeSpan.FromSeconds(settings.SolverTimeoutSeconds);
            var result = await Task.Run(() => solve(h.Graph)).WaitAsync(solverTimeout);

            FindPathMetrics.SolverStatusTotal.WithLabels("success").Inc();

            // Record consent + canary metrics if applicable
            if (result is MaxFlowResponse mfr)
            {
                // Stamp graph block for staleness detection
                mfr.GraphBlock = graphBlock;

                if (mfr.ConsentDroppedPaths > 0)
                    FindPathMetrics.ConsentPathsDroppedTotal.Inc(mfr.ConsentDroppedPaths);

                // A request carrying what-if overrides (simulatedBalances/Trusts/ConsentedAvatars)
                // can legitimately trip input-dependent audit rules (AvatarRegistration etc.) on a
                // hypothetical, not-yet-registered source the frontend is previewing. hasSimulated
                // lets RecordPathAuditViolation tag those excusable violations simulated="true" so
                // alerting excludes previews — solver-integrity rules still page. (Also reused below
                // to skip the on-chain canary.)
                bool hasSimulated = (request.SimulatedBalances?.Count > 0)
                                    || (request.SimulatedTrusts?.Count > 0)
                                    || (request.SimulatedConsentedAvatars?.Count > 0);

                // Path audit: when HubContractValidator detected Hub.sol rule violations OR
                // threw an unexpected exception, the produced path is unsafe (would either
                // revert on-chain or could not be vouched for). Record metrics, log, then
                // replace the broken transfers with the canonical empty response so the
                // wallet can't submit a doomed tx. The API contract still returns
                // MaxFlowResponse — never errors.
                if (mfr.ValidationErrors > 0 || mfr.ValidatorException)
                {
                    log.LogError(
                        "Path audit: validator rejected response — replacing path with empty response. " +
                        "Source={Source}, Sink={Sink}, Block={Block}, TargetFlow={TargetFlow}, Steps={Steps}, " +
                        "Errors={Errors}, ValidatorException={ValidatorException}, simulated={Simulated}, reqId={ReqId}",
                        safeSource, safeSink, mfr.GraphBlock, safeTargetFlow,
                        mfr.Transfers?.Count ?? 0, mfr.ValidationErrors, mfr.ValidatorException,
                        hasSimulated, mfr.ReqId ?? "-");

                    RecordPathAuditViolation(mfr, hasSimulated);

                    var emptyResponse = new MaxFlowResponse("0", new List<TransferPathStep>(), null)
                    {
                        ReqId = mfr.ReqId,
                        GraphBlock = mfr.GraphBlock,
                        ConsentDroppedPaths = mfr.ConsentDroppedPaths,
                        ValidationErrors = mfr.ValidationErrors,
                        ValidationViolationRules = mfr.ValidationViolationRules,
                        ValidatorException = mfr.ValidatorException,
                        ValidationWarnings = mfr.ValidationWarnings,
                        ValidationWarningRules = mfr.ValidationWarningRules,
                    };
                    result = emptyResponse;
                    mfr = emptyResponse;
                }
                if (mfr.ValidatorException)
                    FindPathMetrics.CanaryValidatorExceptionTotal.Inc();

                // Warning-severity violations: observe-only, response NOT replaced.
                // Per-rule counter for alerting; the original response is preserved.
                if (mfr.ValidationWarnings > 0 && mfr.ValidationWarningRules != null)
                {
                    foreach (var rule in mfr.ValidationWarningRules)
                        FindPathMetrics.PathAuditWarningsTotal.WithLabels(rule).Inc();
                }

                // Simulation canary: enqueue for async on-chain validation.
                // Skip when:
                //   - simulated balances/trusts (not real state),
                //   - source is a Group/Organization (Hub.sol operateFlowMatrix requires
                //     isApprovedForAll(source, msg.sender); Groups/Orgs don't self-approve →
                //     false-positive OperatorNotApprovedForSource).
                // withWrap=true is handled via eth_simulateV1 bundle inside the canary:
                // source's Wrapper.unwrap() calls run before operateFlowMatrix in a single
                // shared-state simulation, mirroring how the SDK builds the on-chain tx.
                bool sourceUnsimulatable = false;
                if (!string.IsNullOrEmpty(request.Source)
                    && AddressIdPool.TryIdOf(request.Source.ToLowerInvariant(), out int sourceId))
                {
                    sourceUnsimulatable = h.Graph.IsGroup(sourceId)
                                          || h.Graph.IsOrganization(sourceId);
                }

                bool withWrap = request.WithWrap ?? false;

                if (simulationCanary != null
                    && mfr.Transfers.Count > 0
                    && !string.IsNullOrEmpty(request.Source)
                    && !string.IsNullOrEmpty(request.Sink)
                    && !hasSimulated
                    && !sourceUnsimulatable)
                {
                    Dictionary<string, string>? wrapperMap = null;
                    HashSet<string>? inflationaryWrappers = null;
                    try
                    {
                        if (h.Graph.WrapperToAvatar.Count > 0)
                        {
                            wrapperMap = new Dictionary<string, string>(
                                h.Graph.WrapperToAvatar.Count, StringComparer.OrdinalIgnoreCase);
                            foreach (var kv in h.Graph.WrapperToAvatar)
                            {
                                wrapperMap[AddressIdPool.StringOf(kv.Key)] = AddressIdPool.StringOf(kv.Value);
                            }
                            // Build the inflationary-wrapper subset by resolving wrapper int IDs back
                            // to addresses. Empty set is fine — the canary treats absence as "demurraged"
                            // and skips the conversion step, preserving correctness for non-inflationary paths.
                            if (h.Graph.InflationaryWrappers.Count > 0)
                            {
                                inflationaryWrappers = new HashSet<string>(
                                    h.Graph.InflationaryWrappers.Count, StringComparer.OrdinalIgnoreCase);
                                foreach (var wrapperId in h.Graph.InflationaryWrappers)
                                    inflationaryWrappers.Add(AddressIdPool.StringOf(wrapperId));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Failed to build wrapper map for canary — skipping simulation (would produce false positives)");
                        wrapperMap = null;
                        inflationaryWrappers = null;
                    }

                    if (wrapperMap == null && h.Graph.WrapperToAvatar.Count > 0)
                    {
                        // Wrapper resolution failed — simulation without it would produce
                        // false-positive AvatarMustBeRegistered reverts (and would skip the
                        // unwrap prefix for withWrap=true). Skip entirely and record the
                        // reason so operators can spot map-build failures in metrics.
                        SimulationCanaryService.RecordSkipped("wrapper_map_unavailable");
                    }
                    else
                    {
                        simulationCanary.TryEnqueue(new CanaryWorkItem(
                            ReqId: mfr.ReqId ?? Guid.NewGuid().ToString("N")[..8],
                            Source: request.Source,
                            Sink: request.Sink,
                            GraphBlock: mfr.GraphBlock,
                            Transfers: new List<TransferPathStep>(mfr.Transfers),
                            WrapperToAvatar: wrapperMap,
                            WithWrap: withWrap,
                            InflationaryWrappers: inflationaryWrappers));
                    }
                }

                log.LogInformation(
                    "{Route} source={Source} sink={Sink} targetFlow={TargetFlow} maxFlow={MaxFlow} transfers={Transfers} maxTransfers={MaxTransfers} graphBlock={GraphBlock} durationMs={DurationMs} status={Status} withWrap={WithWrap} quantizedMode={QuantizedMode} fromTokens={FromTokens} toTokens={ToTokens} excludedFromTokens={ExcludedFromTokens} excludedToTokens={ExcludedToTokens} simBalances={SimBalances} simTrusts={SimTrusts} simConsented={SimConsented} reqId={ReqId}",
                    route, safeSource, safeSink, safeTargetFlow,
                    mfr.MaxFlow, mfr.Transfers?.Count ?? 0, request.MaxTransfers ?? -1,
                    graphBlock, sw.ElapsedMilliseconds, 200,
                    request.WithWrap ?? false, request.QuantizedMode ?? false,
                    SanitizeTokenList(request.FromTokens), SanitizeTokenList(request.ToTokens),
                    SanitizeTokenList(request.ExcludedFromTokens), SanitizeTokenList(request.ExcludedToTokens),
                    SimCount(request.SimulatedBalances), SimCount(request.SimulatedTrusts), SimCount(request.SimulatedConsentedAvatars),
                    mfr.ReqId ?? "-");
            }
            else
            {
                // /findMaxFlow returns long, not MaxFlowResponse
                log.LogInformation(
                    "{Route} source={Source} sink={Sink} targetFlow={TargetFlow} maxFlow={MaxFlow} transfers={Transfers} maxTransfers={MaxTransfers} graphBlock={GraphBlock} durationMs={DurationMs} status={Status} withWrap={WithWrap} quantizedMode={QuantizedMode} fromTokens={FromTokens} toTokens={ToTokens} excludedFromTokens={ExcludedFromTokens} excludedToTokens={ExcludedToTokens} simBalances={SimBalances} simTrusts={SimTrusts} simConsented={SimConsented} reqId={ReqId}",
                    route, safeSource, safeSink, safeTargetFlow,
                    result, 0, request.MaxTransfers ?? -1,
                    graphBlock, sw.ElapsedMilliseconds, 200,
                    request.WithWrap ?? false, request.QuantizedMode ?? false,
                    SanitizeTokenList(request.FromTokens), SanitizeTokenList(request.ToTokens),
                    SanitizeTokenList(request.ExcludedFromTokens), SanitizeTokenList(request.ExcludedToTokens),
                    SimCount(request.SimulatedBalances), SimCount(request.SimulatedTrusts), SimCount(request.SimulatedConsentedAvatars),
                    "-");
            }

            return Results.Ok(result);
        }
        catch (TimeoutException)
        {
            FindPathMetrics.SolverStatusTotal.WithLabels("timeout").Inc();
            log.LogError(
                "{Route} source={Source} sink={Sink} targetFlow={TargetFlow} maxFlow={MaxFlow} transfers={Transfers} maxTransfers={MaxTransfers} graphBlock={GraphBlock} durationMs={DurationMs} status={Status} withWrap={WithWrap} quantizedMode={QuantizedMode} error={Error}",
                route, safeSource, safeSink, safeTargetFlow,
                "", 0, request.MaxTransfers ?? -1,
                graphBlock, sw.ElapsedMilliseconds, 504,
                request.WithWrap ?? false, request.QuantizedMode ?? false, "timeout");
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch (ArgumentException ex)
        {
            FindPathMetrics.SolverStatusTotal.WithLabels("bad_request").Inc();
            log.LogWarning(ex,
                "{Route} source={Source} sink={Sink} targetFlow={TargetFlow} maxFlow={MaxFlow} transfers={Transfers} maxTransfers={MaxTransfers} graphBlock={GraphBlock} durationMs={DurationMs} status={Status} withWrap={WithWrap} quantizedMode={QuantizedMode} error={Error}",
                route, safeSource, safeSink, safeTargetFlow,
                "", 0, request.MaxTransfers ?? -1,
                graphBlock, sw.ElapsedMilliseconds, 400,
                request.WithWrap ?? false, request.QuantizedMode ?? false, "bad_request");
            return Results.BadRequest(ex.Message);
        }
        catch (GraphNotReadyException ex)
        {
            // Backstops the check-then-Rent TOCTOU window after the
            // HasCurrentSnapshot gate above. Scoped to the dedicated
            // GraphNotReadyException so genuine InvalidOperationExceptions from
            // the solver still surface as 500 via the generic catch below.
            FindPathMetrics.SolverStatusTotal.WithLabels("not_ready").Inc();
            log.LogWarning(ex,
                "{Route} source={Source} sink={Sink} targetFlow={TargetFlow} maxFlow={MaxFlow} transfers={Transfers} maxTransfers={MaxTransfers} graphBlock={GraphBlock} durationMs={DurationMs} status={Status} withWrap={WithWrap} quantizedMode={QuantizedMode} error={Error}",
                route, safeSource, safeSink, safeTargetFlow,
                "", 0, request.MaxTransfers ?? -1,
                graphBlock, sw.ElapsedMilliseconds, 503,
                request.WithWrap ?? false, request.QuantizedMode ?? false, "not_ready");
            return Results.Text("Graphs are not loaded yet.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FindPathMetrics.SolverStatusTotal.WithLabels("error").Inc();
            log.LogError(ex,
                "{Route} source={Source} sink={Sink} targetFlow={TargetFlow} maxFlow={MaxFlow} transfers={Transfers} maxTransfers={MaxTransfers} graphBlock={GraphBlock} durationMs={DurationMs} status={Status} withWrap={WithWrap} quantizedMode={QuantizedMode} error={Error}",
                route, safeSource, safeSink, safeTargetFlow,
                "", 0, request.MaxTransfers ?? -1,
                graphBlock, sw.ElapsedMilliseconds, 500,
                request.WithWrap ?? false, request.QuantizedMode ?? false, "exception");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
        finally
        {
            semaphore.Release();
            FindPathMetrics.InFlightRequestsGauge.Dec();
        }
    }

    /// <summary>
    /// Execute pathfinding against a historical block-filtered graph.
    /// Uses the same semaphore and timeout as live requests.
    /// Simulation canary is intentionally skipped — historical paths compute against past state,
    /// not current on-chain state, so eth_call validation would be meaningless.
    /// </summary>
    private async Task<IResult> ExecuteHistorical(
        string route,
        FlowRequest request,
        long blockNumber,
        Func<CapacityGraph, object> solve)
    {
        if (!semaphore.Wait(0))
        {
            FindPathMetrics.RejectedRequestsCounter.Inc();
            log.LogWarning("Concurrency limit hit — historical request rejected");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var sw = Stopwatch.StartNew();
        var safeSource = SanitizeForLog(request.Source);
        var safeSink = SanitizeForLog(request.Sink);
        var safeTargetFlow = SanitizeForLog(request.TargetFlow);

        FindPathMetrics.InFlightRequestsGauge.Inc();
        try
        {
            log.LogInformation(
                "Historical {Route}: loading graph at block {Block} for {Source} → {Sink}",
                route, blockNumber, safeSource, safeSink);

            var factory = await historicalGraphCache.GetOrLoadFactoryAsync(blockNumber);

            var trustGraph = factory.V2TrustGraph();
            var balanceGraph = factory.V2BalanceGraph();
            var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

            var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
            capacityGraph.Block = blockNumber;

            var solverTimeout = TimeSpan.FromSeconds(settings.SolverTimeoutSeconds);
            var result = await Task.Run(() => solve(capacityGraph)).WaitAsync(solverTimeout);

            FindPathMetrics.SolverStatusTotal.WithLabels("success").Inc();

            if (result is MaxFlowResponse mfr)
            {
                mfr.GraphBlock = blockNumber;

                // Track consent metrics (same as live path)
                if (mfr.ConsentDroppedPaths > 0)
                    FindPathMetrics.ConsentPathsDroppedTotal.Inc(mfr.ConsentDroppedPaths);

                // Input-dependent rules can be excused per-rule when simulated (see live path);
                // solver-integrity rules still page. Historical (X-Max-Block-Number) requests can
                // carry simulatedBalances/Trusts too.
                bool hasSimulated = (request.SimulatedBalances?.Count > 0)
                                    || (request.SimulatedTrusts?.Count > 0)
                                    || (request.SimulatedConsentedAvatars?.Count > 0);

                // Track Hub.sol rule violations or validator exceptions (same as live path)
                // — and replace with empty.
                if (mfr.ValidationErrors > 0 || mfr.ValidatorException)
                {
                    log.LogError(
                        "Historical path audit: validator rejected response — replacing path with empty response. " +
                        "Source={Source}, Sink={Sink}, Block={Block}, TargetFlow={TargetFlow}, Steps={Steps}, " +
                        "Errors={Errors}, ValidatorException={ValidatorException}, simulated={Simulated}, reqId={ReqId}",
                        safeSource, safeSink, blockNumber, safeTargetFlow,
                        mfr.Transfers?.Count ?? 0, mfr.ValidationErrors, mfr.ValidatorException,
                        hasSimulated, mfr.ReqId ?? "-");

                    RecordPathAuditViolation(mfr, hasSimulated);

                    var emptyResponse = new MaxFlowResponse("0", new List<TransferPathStep>(), null)
                    {
                        ReqId = mfr.ReqId,
                        GraphBlock = mfr.GraphBlock,
                        ConsentDroppedPaths = mfr.ConsentDroppedPaths,
                        ValidationErrors = mfr.ValidationErrors,
                        ValidationViolationRules = mfr.ValidationViolationRules,
                        ValidatorException = mfr.ValidatorException,
                        ValidationWarnings = mfr.ValidationWarnings,
                        ValidationWarningRules = mfr.ValidationWarningRules,
                    };
                    result = emptyResponse;
                    mfr = emptyResponse;
                }

                // Track validator exceptions on the historical path the same way the
                // live path does — otherwise validator bugs disappear from telemetry
                // whenever a request uses X-Max-Block-Number.
                if (mfr.ValidatorException)
                    FindPathMetrics.CanaryValidatorExceptionTotal.Inc();

                // Mirror the live-path's warning bucket on the historical path so
                // diagnostic rules show up in telemetry for X-Max-Block-Number requests too.
                if (mfr.ValidationWarnings > 0 && mfr.ValidationWarningRules != null)
                {
                    foreach (var rule in mfr.ValidationWarningRules)
                        FindPathMetrics.PathAuditWarningsTotal.WithLabels(rule).Inc();
                }

                log.LogInformation(
                    "{Route} source={Source} sink={Sink} targetFlow={TargetFlow} maxFlow={MaxFlow} transfers={Transfers} maxTransfers={MaxTransfers} graphBlock={GraphBlock} durationMs={DurationMs} status={Status} withWrap={WithWrap} quantizedMode={QuantizedMode} fromTokens={FromTokens} toTokens={ToTokens} excludedFromTokens={ExcludedFromTokens} excludedToTokens={ExcludedToTokens} simBalances={SimBalances} simTrusts={SimTrusts} simConsented={SimConsented} reqId={ReqId}",
                    route, safeSource, safeSink, safeTargetFlow,
                    mfr.MaxFlow, mfr.Transfers?.Count ?? 0, request.MaxTransfers ?? -1,
                    blockNumber, sw.ElapsedMilliseconds, 200,
                    request.WithWrap ?? false, request.QuantizedMode ?? false,
                    SanitizeTokenList(request.FromTokens), SanitizeTokenList(request.ToTokens),
                    SanitizeTokenList(request.ExcludedFromTokens), SanitizeTokenList(request.ExcludedToTokens),
                    SimCount(request.SimulatedBalances), SimCount(request.SimulatedTrusts), SimCount(request.SimulatedConsentedAvatars),
                    mfr.ReqId ?? "-");
            }
            else
            {
                log.LogInformation(
                    "{Route} source={Source} sink={Sink} targetFlow={TargetFlow} maxFlow={MaxFlow} transfers={Transfers} maxTransfers={MaxTransfers} graphBlock={GraphBlock} durationMs={DurationMs} status={Status} withWrap={WithWrap} quantizedMode={QuantizedMode} fromTokens={FromTokens} toTokens={ToTokens} excludedFromTokens={ExcludedFromTokens} excludedToTokens={ExcludedToTokens} simBalances={SimBalances} simTrusts={SimTrusts} simConsented={SimConsented} reqId={ReqId}",
                    route, safeSource, safeSink, safeTargetFlow,
                    result, 0, request.MaxTransfers ?? -1,
                    blockNumber, sw.ElapsedMilliseconds, 200,
                    request.WithWrap ?? false, request.QuantizedMode ?? false,
                    SanitizeTokenList(request.FromTokens), SanitizeTokenList(request.ToTokens),
                    SanitizeTokenList(request.ExcludedFromTokens), SanitizeTokenList(request.ExcludedToTokens),
                    SimCount(request.SimulatedBalances), SimCount(request.SimulatedTrusts), SimCount(request.SimulatedConsentedAvatars),
                    "-");
            }

            return Results.Ok(result);
        }
        catch (TimeoutException)
        {
            FindPathMetrics.SolverStatusTotal.WithLabels("timeout").Inc();
            log.LogError(
                "{Route} source={Source} sink={Sink} targetFlow={TargetFlow} maxFlow={MaxFlow} transfers={Transfers} maxTransfers={MaxTransfers} graphBlock={GraphBlock} durationMs={DurationMs} status={Status} withWrap={WithWrap} quantizedMode={QuantizedMode} error={Error}",
                route, safeSource, safeSink, safeTargetFlow,
                "", 0, request.MaxTransfers ?? -1,
                blockNumber, sw.ElapsedMilliseconds, 504,
                request.WithWrap ?? false, request.QuantizedMode ?? false, "timeout");
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch (ArgumentException ex)
        {
            FindPathMetrics.SolverStatusTotal.WithLabels("bad_request").Inc();
            log.LogWarning(ex,
                "{Route} source={Source} sink={Sink} targetFlow={TargetFlow} maxFlow={MaxFlow} transfers={Transfers} maxTransfers={MaxTransfers} graphBlock={GraphBlock} durationMs={DurationMs} status={Status} withWrap={WithWrap} quantizedMode={QuantizedMode} error={Error}",
                route, safeSource, safeSink, safeTargetFlow,
                "", 0, request.MaxTransfers ?? -1,
                blockNumber, sw.ElapsedMilliseconds, 400,
                request.WithWrap ?? false, request.QuantizedMode ?? false, "bad_request");
            return Results.BadRequest(ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FindPathMetrics.SolverStatusTotal.WithLabels("error").Inc();
            log.LogError(ex,
                "{Route} source={Source} sink={Sink} targetFlow={TargetFlow} maxFlow={MaxFlow} transfers={Transfers} maxTransfers={MaxTransfers} graphBlock={GraphBlock} durationMs={DurationMs} status={Status} withWrap={WithWrap} quantizedMode={QuantizedMode} error={Error}",
                route, safeSource, safeSink, safeTargetFlow,
                "", 0, request.MaxTransfers ?? -1,
                blockNumber, sw.ElapsedMilliseconds, 500,
                request.WithWrap ?? false, request.QuantizedMode ?? false, "exception");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
        finally
        {
            semaphore.Release();
            FindPathMetrics.InFlightRequestsGauge.Dec();
        }
    }
}
