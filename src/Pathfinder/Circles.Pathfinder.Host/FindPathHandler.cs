using System.Diagnostics;
using System.Text.Json;
using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host.Canary;
using Circles.Pathfinder.Host.State;
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
        return new FlowRequest
        {
            Source = from.ToLowerInvariant(),
            Sink = to.ToLowerInvariant(),
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
            var solverTimeout = TimeSpan.FromSeconds(settings.SolverTimeoutSeconds);
            var result = await Task.Run(() => solve(h.Graph)).WaitAsync(solverTimeout);

            FindPathMetrics.SolverStatusTotal.WithLabels("success").Inc();

            // Record consent + canary metrics if applicable
            if (result is MaxFlowResponse mfr)
            {
                // Stamp graph block for staleness detection (use graph's own block, not
                // state.Current.Block which may have advanced during solving)
                mfr.GraphBlock = h.Graph.Block;

                if (mfr.ConsentDroppedPaths > 0)
                    FindPathMetrics.ConsentPathsDroppedTotal.Inc(mfr.ConsentDroppedPaths);
                if (mfr.ConsentSafetyNetRejected > 0)
                    FindPathMetrics.ConsentSafetyNetTriggeredTotal.Inc(mfr.ConsentSafetyNetRejected);

                // Canary: record Hub.sol rule violations (observe-only)
                if (mfr.ValidationErrors > 0)
                {
                    FindPathMetrics.CanaryValidationFailureTotal.WithLabels("any").Inc();
                    if (mfr.ValidationViolationRules != null)
                    {
                        foreach (var rule in mfr.ValidationViolationRules)
                            FindPathMetrics.CanaryValidationFailureTotal.WithLabels(rule).Inc();
                    }
                }

                // Simulation canary: enqueue for async eth_call validation
                if (simulationCanary != null
                    && mfr.Transfers.Count > 0
                    && !string.IsNullOrEmpty(request.Source)
                    && !string.IsNullOrEmpty(request.Sink))
                {
                    simulationCanary.TryEnqueue(new CanaryWorkItem(
                        ReqId: mfr.ReqId ?? Guid.NewGuid().ToString("N")[..8],
                        Source: request.Source,
                        Sink: request.Sink,
                        GraphBlock: mfr.GraphBlock,
                        Transfers: new List<TransferPathStep>(mfr.Transfers))); // defensive copy
                }
            }

            return Results.Ok(result);
        }
        catch (TimeoutException)
        {
            FindPathMetrics.SolverStatusTotal.WithLabels("timeout").Inc();
            log.LogError("{Route} solver timed out after {Timeout}s for request: from={From}, to={To}, amount={Amount}",
                route, settings.SolverTimeoutSeconds, request.Source, request.Sink, request.TargetFlow);
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch (ArgumentException ex)
        {
            FindPathMetrics.SolverStatusTotal.WithLabels("bad_request").Inc();
            log.LogWarning(ex, "{Route} validation error: {Message}", route, ex.Message);
            return Results.BadRequest(ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FindPathMetrics.SolverStatusTotal.WithLabels("error").Inc();
            log.LogError(ex, "{Route} threw exception for request: from={From}, to={To}, amount={Amount}",
                route, request.Source, request.Sink, request.TargetFlow);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
        finally
        {
            semaphore.Release();
            FindPathMetrics.InFlightRequestsGauge.Dec();
        }
    }
}
