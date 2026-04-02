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
    internal static (List<SimulatedBalance>? sim, IResult? error) ParseSimulatedBalances(string? json)
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
    /// Execute a solver call with semaphore guarding, graph rent, timeout, metrics, and exception handling.
    /// The <paramref name="solve"/> delegate receives the rented capacity graph and returns the result to serialize.
    /// </summary>
    /// <summary>
    /// Strip CR/LF from user-supplied values before logging (prevents log forging).
    /// </summary>
    private static string? SanitizeForLog(string? value) =>
        value?.Replace("\r", string.Empty).Replace("\n", string.Empty);

    internal async Task<IResult> ExecuteWithGuard(
        string route,
        FlowRequest request,
        NetworkState state,
        CapacityGraphPool pool,
        Func<CapacityGraph, object> solve)
    {
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
                log.LogWarning("Graphs not ready");
                return Results.BadRequest("Graphs are not loaded yet.");
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
                if (mfr.ConsentSafetyNetRejected > 0)
                    FindPathMetrics.ConsentSafetyNetTriggeredTotal.Inc(mfr.ConsentSafetyNetRejected);

                // Path audit: record Hub.sol rule violations (observe-only, alert via Prometheus)
                if (mfr.ValidationErrors > 0)
                {
                    FindPathMetrics.PathAuditViolationsTotal.WithLabels("any").Inc();
                    if (mfr.ValidationViolationRules != null)
                    {
                        foreach (var rule in mfr.ValidationViolationRules)
                            FindPathMetrics.PathAuditViolationsTotal.WithLabels(rule).Inc();
                    }

                    log.LogError(
                        "Path audit: Hub.sol rule violations detected — path may revert on-chain. " +
                        "Source={Source}, Sink={Sink}, Block={Block}, Steps={Steps}, Errors={Errors}",
                        safeSource, safeSink, mfr.GraphBlock,
                        mfr.Transfers?.Count ?? 0, mfr.ValidationErrors);
                }

                // Simulation canary: enqueue for async eth_call validation.
                // Skip when: simulated balances/trusts (not real state), or source is a Group/Organization
                // (Hub.sol operateFlowMatrix requires isApprovedForAll(source, msg.sender).
                // Groups/Orgs don't self-approve — canary would get OperatorNotApprovedForSource).
                bool hasSimulated = (request.SimulatedBalances?.Count > 0)
                                    || (request.SimulatedTrusts?.Count > 0)
                                    || (request.SimulatedConsentedAvatars?.Count > 0);

                bool sourceUnsimulatable = false;
                if (!string.IsNullOrEmpty(request.Source)
                    && AddressIdPool.TryIdOf(request.Source.ToLowerInvariant(), out int sourceId))
                {
                    sourceUnsimulatable = h.Graph.IsGroup(sourceId)
                                          || h.Graph.IsOrganization(sourceId);
                }

                if (simulationCanary != null
                    && mfr.Transfers.Count > 0
                    && !string.IsNullOrEmpty(request.Source)
                    && !string.IsNullOrEmpty(request.Sink)
                    && !hasSimulated
                    && !sourceUnsimulatable)
                {
                    Dictionary<string, string>? wrapperMap = null;
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
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Failed to build wrapper map for canary — simulation will run without wrapper resolution");
                        wrapperMap = null;
                    }

                    simulationCanary.TryEnqueue(new CanaryWorkItem(
                        ReqId: mfr.ReqId ?? Guid.NewGuid().ToString("N")[..8],
                        Source: request.Source,
                        Sink: request.Sink,
                        GraphBlock: mfr.GraphBlock,
                        Transfers: new List<TransferPathStep>(mfr.Transfers),
                        WrapperToAvatar: wrapperMap));
                }

                log.LogInformation(
                    "{Route} source={Source} sink={Sink} targetFlow={TargetFlow} maxFlow={MaxFlow} transfers={Transfers} maxTransfers={MaxTransfers} graphBlock={GraphBlock} durationMs={DurationMs} status={Status} withWrap={WithWrap} quantizedMode={QuantizedMode}",
                    route, safeSource, safeSink, safeTargetFlow,
                    mfr.MaxFlow, mfr.Transfers?.Count ?? 0, request.MaxTransfers ?? -1,
                    graphBlock, sw.ElapsedMilliseconds, 200,
                    request.WithWrap ?? false, request.QuantizedMode ?? false);
            }
            else
            {
                // /findMaxFlow returns long, not MaxFlowResponse
                log.LogInformation(
                    "{Route} source={Source} sink={Sink} targetFlow={TargetFlow} maxFlow={MaxFlow} transfers={Transfers} maxTransfers={MaxTransfers} graphBlock={GraphBlock} durationMs={DurationMs} status={Status} withWrap={WithWrap} quantizedMode={QuantizedMode}",
                    route, safeSource, safeSink, safeTargetFlow,
                    result, 0, request.MaxTransfers ?? -1,
                    graphBlock, sw.ElapsedMilliseconds, 200,
                    request.WithWrap ?? false, request.QuantizedMode ?? false);
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
}
