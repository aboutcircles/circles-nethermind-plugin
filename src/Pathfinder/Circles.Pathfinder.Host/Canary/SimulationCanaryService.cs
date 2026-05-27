using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using System.Threading.Channels;
using Circles.Common.Dto;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Simulation;
using Prometheus;

namespace Circles.Pathfinder.Host.Canary;

/// <summary>
/// Work item enqueued by FindPathHandler for background simulation.
/// </summary>
internal sealed record CanaryWorkItem(
    string ReqId,
    string Source,
    string Sink,
    long GraphBlock,
    List<TransferPathStep> Transfers,
    IReadOnlyDictionary<string, string>? WrapperToAvatar = null,
    bool WithWrap = false,
    // Subset of WrapperToAvatar keys (lowercased addresses) whose CrcV2_ERC20WrapperDeployed.circlesType == 1.
    // The canary's unwrap-prefix resolver uses this to discriminate the `_amount` unit semantic:
    // - DemurrageCircles (NOT in this set): unwrap(_amount) takes demurraged 1155 units; pass BuildUnwrapPrefix's sum directly.
    // - InflationaryCircles (IN this set):  unwrap(_amount) takes inflationary ERC20 units; convert via convertDemurrageToInflationaryValue.
    IReadOnlySet<string>? InflationaryWrappers = null);

/// <summary>
/// Background service that simulates pathfinder results via eth_call against the local
/// Nethermind node. Detects on-chain reverts that the pathfinder missed.
///
/// Architecture: bounded Channel (fire-and-forget, never blocks the response path).
/// If the queue is full, the new work item is dropped (canary is best-effort).
/// </summary>
internal sealed class SimulationCanaryService : BackgroundService
{
    private readonly Channel<CanaryWorkItem> _channel;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SimulationCanaryService> _log;
    private readonly string _rpcUrl;

    // Metrics
    private static readonly Counter SimulationTotal = Metrics.CreateCounter(
        "circles_canary_simulation_total",
        "Total eth_call/eth_simulateV1 simulations attempted",
        new CounterConfiguration { LabelNames = new[] { "result", "prefix" } });

    private static readonly Counter SimulationRevertTotal = Metrics.CreateCounter(
        "circles_canary_simulation_revert_total",
        "Simulated reverts by category, label, and which call in the bundle reverted",
        new CounterConfiguration { LabelNames = new[] { "category", "label", "stage" } });

    private static readonly Histogram SimulationDuration = Metrics.CreateHistogram(
        "circles_canary_simulation_duration_seconds",
        "eth_call simulation duration",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.005, 2, 10) });

    private static readonly Gauge QueueDepth = Metrics.CreateGauge(
        "circles_canary_simulation_queue_depth",
        "Current number of pending simulation work items");

    private static readonly Counter QueueDropped = Metrics.CreateCounter(
        "circles_canary_simulation_queue_dropped_total",
        "Work items dropped because the simulation queue was full");

    private static readonly Counter SimulationSkipped = Metrics.CreateCounter(
        "circles_canary_simulation_skipped_total",
        "Work items skipped before enqueue, by reason",
        new CounterConfiguration { LabelNames = new[] { "reason" } });

    public static void RecordSkipped(string reason) => SimulationSkipped.WithLabels(reason).Inc();

    public SimulationCanaryService(
        Settings settings,
        ILogger<SimulationCanaryService> log,
        IHttpClientFactory httpClientFactory)
    {
        _log = log;
        _httpClientFactory = httpClientFactory;
        _rpcUrl = settings.NethermindRpcUrl;

        int queueSize = int.TryParse(
            Environment.GetEnvironmentVariable("CANARY_SIMULATION_QUEUE_SIZE"), out var qs) ? qs : 10;

        // DropWrite: when queue is full, TryWrite returns false for the NEW item.
        // Oldest items are more valuable (closer to the graph block actually served).
        _channel = Channel.CreateBounded<CanaryWorkItem>(new BoundedChannelOptions(queueSize)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });
    }

    /// <summary>
    /// Enqueue a work item for background simulation. Returns false if queue is full (dropped).
    /// </summary>
    public bool TryEnqueue(CanaryWorkItem item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            QueueDepth.Set(_channel.Reader.Count);
            return true;
        }

        QueueDropped.Inc();
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("SimulationCanary started — rpcUrl={RpcUrl}, queue listening", _rpcUrl);

        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            QueueDepth.Set(_channel.Reader.Count);

            try
            {
                await SimulateAsync(item, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "[{ReqId}] SimulationCanary: unexpected error during simulation", item.ReqId);
                SimulationTotal.WithLabels("error", "none").Inc();
            }
        }
    }

    private async Task SimulateAsync(CanaryWorkItem item, CancellationToken ct)
    {
        // Compute unwrap prefix once — the same list is needed to decide which RPC method to use
        // AND to populate the bundle when withWrap=true. Resolve TokenOwners using the same map
        // so the operateFlowMatrix calldata references avatar addresses, not wrappers.
        IReadOnlyList<UnwrapCall> unwrapCalls;
        string calldata;
        try
        {
            unwrapCalls = item.WithWrap
                ? BuildUnwrapPrefix(item.Transfers, item.WrapperToAvatar, item.InflationaryWrappers)
                : Array.Empty<UnwrapCall>();
            var transfers = ResolveWrapperTokenOwners(item.Transfers, item.WrapperToAvatar);
            calldata = FlowMatrixEncoder.BuildCalldata(item.Source, item.Sink, transfers);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[{ReqId}] SimulationCanary: calldata encoding failed from={Source} to={Sink} block={Block} steps={Steps}",
                item.ReqId, item.Source, item.Sink, item.GraphBlock, item.Transfers.Count);
            SimulationTotal.WithLabels("encode_error", "none").Inc();
            return;
        }

        // Empty unwrap list ⇒ no wrapper-form supply in this path. Fall back to plain eth_call
        // even when withWrap=true was requested — the unwrap-prefix code path adds nothing here
        // and eth_call has wider node-version support.
        bool useUnwrapBundle = unwrapCalls.Count > 0;
        string prefixLabel = useUnwrapBundle ? "unwrap" : "none";

        using var timer = SimulationDuration.NewTimer();

        try
        {
            using var client = _httpClientFactory.CreateClient("canary-simulation");
            // Simulate at the exact block the graph was built from — eliminates false
            // positives from chain state drift between graph build and simulation.
            var blockTag = item.GraphBlock > 0 ? $"0x{item.GraphBlock:x}" : "latest";
            if (item.GraphBlock <= 0)
                _log.LogWarning("[{ReqId}] SimulationCanary: GraphBlock={Block} — simulating at 'latest', results may not match served graph",
                    item.ReqId, item.GraphBlock);

            if (useUnwrapBundle)
            {
                // Inflationary wrappers need demurraged → inflationary conversion before unwrap().
                // DemurrageCircles wrappers pass through unchanged. Resolver is a no-op (zero RPC
                // overhead) when the bundle contains no inflationary wrappers — the common case.
                bool hasInflationary = false;
                for (int i = 0; i < unwrapCalls.Count; i++)
                {
                    if (unwrapCalls[i].IsInflationary) { hasInflationary = true; break; }
                }

                if (hasInflationary)
                {
                    unwrapCalls = await ResolveInflationaryAmountsAsync(item, unwrapCalls, blockTag, client, ct);
                }

                await SimulateBundleAsync(item, unwrapCalls, calldata, blockTag, prefixLabel, client, ct);
                QueueDepth.Set(_channel.Reader.Count);
                return;
            }

            var rpcRequest = new
            {
                jsonrpc = "2.0",
                method = "eth_call",
                @params = new object[]
                {
                    new { from = item.Source, to = FlowMatrixEncoder.CirclesHubAddress, data = calldata },
                    blockTag
                },
                id = 1
            };

            var response = await client.PostAsJsonAsync(_rpcUrl, rpcRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "[{ReqId}] SimulationCanary: eth_call HTTP {StatusCode} from={Source} to={Sink}",
                    item.ReqId, (int)response.StatusCode, item.Source, item.Sink);
                SimulationTotal.WithLabels("rpc_http_error", prefixLabel).Inc();
                return;
            }

            JsonElement json;
            try
            {
                json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            }
            catch (JsonException jex)
            {
                _log.LogWarning(jex,
                    "[{ReqId}] SimulationCanary: non-JSON response from={Source} to={Sink}",
                    item.ReqId, item.Source, item.Sink);
                SimulationTotal.WithLabels("rpc_parse_error", prefixLabel).Inc();
                return;
            }

            if (json.ValueKind == JsonValueKind.Undefined || json.ValueKind == JsonValueKind.Null)
            {
                _log.LogWarning(
                    "[{ReqId}] SimulationCanary: empty/null response from eth_call — node may be misconfigured",
                    item.ReqId);
                SimulationTotal.WithLabels("rpc_empty_response", prefixLabel).Inc();
                return;
            }

            if (json.TryGetProperty("error", out var error))
            {
                var revertData = error.TryGetProperty("data", out var d) ? d.GetString() : null;
                var revertMsg = error.TryGetProperty("message", out var m) ? m.GetString() : "unknown";

                var (category, label) = RevertClassifier.Classify(revertData ?? revertMsg);

                SimulationTotal.WithLabels("revert", prefixLabel).Inc();
                SimulationRevertTotal.WithLabels(category, label, "flow_matrix").Inc();

                // Full replay context: everything needed to reproduce (incl. calldata
                // so the canary log alone is sufficient to feed scripts/_resources/run-sim.sh).
                _log.LogError(
                    "[{ReqId}] SimulationCanary: REVERT category={Category} label={Label} " +
                    "from={Source} to={Sink} graphBlock={Block} simBlock={SimBlock} steps={Steps} revert={Revert} calldata={Calldata}",
                    item.ReqId, category, label,
                    item.Source, item.Sink, item.GraphBlock, blockTag,
                    item.Transfers.Count, revertMsg, calldata);

                if (revertData == null && revertMsg?.Contains("revert", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _log.LogWarning(
                        "[{ReqId}] SimulationCanary: revert has message but no data field — classification may be degraded",
                        item.ReqId);
                }
            }
            else
            {
                SimulationTotal.WithLabels("success", prefixLabel).Inc();
            }
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // propagate shutdown
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[{ReqId}] SimulationCanary: eth_call failed (network/timeout) from={Source} to={Sink}",
                item.ReqId, item.Source, item.Sink);
            SimulationTotal.WithLabels("rpc_error", prefixLabel).Inc();
        }

        QueueDepth.Set(_channel.Reader.Count);
    }

    /// <summary>
    /// `function unwrap(uint256)` ABI selector — keccak256("unwrap(uint256)")[:4].
    /// </summary>
    private const string UnwrapSelector = "0xde0e9a3e";

    /// <summary>
    /// One unwrap call in the eth_simulateV1 bundle: caller (from) unwraps `amount` of the
    /// wrapped ERC20 at `wrapper`, which mints the underlying 1155 to caller on Hub.sol.
    /// <para><b>Amount unit semantics depend on the wrapper's circlesType:</b></para>
    /// <list type="bullet">
    ///   <item>DemurrageCircles (<see cref="IsInflationary"/> = false): <c>Amount</c> is in
    ///     demurraged 1155 units. unwrap(_amount) burns _amount ERC20 AND credits _amount of
    ///     the underlying 1155 (1:1). BuildUnwrapPrefix's raw sum is correct here.</item>
    ///   <item>InflationaryCircles (<see cref="IsInflationary"/> = true): <c>Amount</c> must be
    ///     in inflationary ERC20 units before reaching unwrap(). unwrap(_amount) burns _amount
    ///     ERC20 and credits <c>_amount * γ^day</c> of 1155. BuildUnwrapPrefix produces a
    ///     demurraged sum; the resolver converts it via wrapper.convertDemurrageToInflationaryValue
    ///     before bundle assembly.</item>
    /// </list>
    /// Verified on-chain by direct eth_simulateV1 probes against both wrapper types (2026-05-27).
    /// </summary>
    internal readonly record struct UnwrapCall(string From, string Wrapper, BigInteger Amount, bool IsInflationary = false);

    /// <summary>
    /// For each transfer whose TokenOwner is a known wrapper, attribute one unwrap call
    /// to the transfer's From address for the full transfer value. Groups identical
    /// (from, wrapper) pairs and sums amounts so a single SDK-equivalent unwrap precedes
    /// the operateFlowMatrix call in the bundle.
    /// <para>The <paramref name="inflationaryWrappers"/> set discriminates which wrappers
    /// need <c>convertDemurrageToInflationaryValue</c> at resolve-time. Wrappers absent
    /// from the set are treated as DemurrageCircles (unit pass-through).</para>
    /// </summary>
    internal static IReadOnlyList<UnwrapCall> BuildUnwrapPrefix(
        List<TransferPathStep> transfers,
        IReadOnlyDictionary<string, string>? wrapperToAvatar,
        IReadOnlySet<string>? inflationaryWrappers = null)
    {
        if (wrapperToAvatar == null || wrapperToAvatar.Count == 0)
            return Array.Empty<UnwrapCall>();

        // Deterministic ordering: pathfinder emits transfers in pipeline order, and we
        // want unwraps grouped by (from, wrapper). A list-of-keys preserves first-seen
        // order across the bundle's call array.
        var sums = new Dictionary<(string From, string Wrapper), BigInteger>();
        var order = new List<(string From, string Wrapper)>();
        foreach (var t in transfers)
        {
            var tokenOwner = t.TokenOwner.ToLowerInvariant();
            if (!wrapperToAvatar.ContainsKey(tokenOwner))
                continue;

            var key = (From: t.From.ToLowerInvariant(), Wrapper: tokenOwner);
            if (!BigInteger.TryParse(t.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                || value <= 0)
                continue;

            if (sums.TryGetValue(key, out var existing))
            {
                sums[key] = existing + value;
            }
            else
            {
                sums[key] = value;
                order.Add(key);
            }
        }

        if (order.Count == 0)
            return Array.Empty<UnwrapCall>();

        var calls = new List<UnwrapCall>(order.Count);
        foreach (var key in order)
        {
            bool isInflationary = inflationaryWrappers != null && inflationaryWrappers.Contains(key.Wrapper);
            calls.Add(new UnwrapCall(key.From, key.Wrapper, sums[key], isInflationary));
        }

        return calls;
    }

    /// <summary>
    /// Encodes a single `unwrap(uint256)` calldata hex string.
    /// </summary>
    internal static string EncodeUnwrapCalldata(BigInteger amount)
    {
        // uint256 cannot represent negatives; BuildUnwrapPrefix already filters value <= 0,
        // so a negative here means a programming error, not a path with weird supply.
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "uint256 amount must be non-negative");

        // BigInteger.ToString("x") prepends a leading 0 on positive numbers whose top
        // nibble is ≥0x8 to avoid sign ambiguity. Trim it so the left-pad math is correct.
        var hex = amount.ToString("x", CultureInfo.InvariantCulture);
        if (hex.Length > 0 && hex[0] == '0' && hex.Length > 1) hex = hex.TrimStart('0');
        if (hex.Length == 0) hex = "0";
        return UnwrapSelector + hex.PadLeft(64, '0');
    }

    /// <summary>
    /// Executes the unwrap-prefix bundle via Nethermind's eth_simulateV1. Sequences
    /// the unwrap calls then the operateFlowMatrix call; state is shared across calls
    /// within a single blockStateCalls entry, mirroring the SDK's actual broadcast order.
    /// </summary>
    private async Task SimulateBundleAsync(
        CanaryWorkItem item,
        IReadOnlyList<UnwrapCall> unwrapCalls,
        string flowMatrixCalldata,
        string blockTag,
        string prefixLabel,
        HttpClient client,
        CancellationToken ct)
    {
        var calls = new List<object>(unwrapCalls.Count + 1);
        foreach (var u in unwrapCalls)
        {
            calls.Add(new
            {
                from = u.From,
                to = u.Wrapper,
                data = EncodeUnwrapCalldata(u.Amount)
            });
        }

        calls.Add(new
        {
            from = item.Source,
            to = FlowMatrixEncoder.CirclesHubAddress,
            data = flowMatrixCalldata
        });

        var rpcRequest = new
        {
            jsonrpc = "2.0",
            method = "eth_simulateV1",
            @params = new object[]
            {
                new
                {
                    blockStateCalls = new[] { new { calls } },
                    validation = false,
                    traceTransfers = false
                },
                blockTag
            },
            id = 1
        };

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(_rpcUrl, rpcRequest, ct);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[{ReqId}] SimulationCanary: eth_simulateV1 failed (network/timeout) from={Source} to={Sink}",
                item.ReqId, item.Source, item.Sink);
            SimulationTotal.WithLabels("rpc_error", prefixLabel).Inc();
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "[{ReqId}] SimulationCanary: eth_simulateV1 HTTP {StatusCode} from={Source} to={Sink}",
                item.ReqId, (int)response.StatusCode, item.Source, item.Sink);
            SimulationTotal.WithLabels("rpc_http_error", prefixLabel).Inc();
            return;
        }

        JsonElement json;
        try
        {
            json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (JsonException jex)
        {
            _log.LogWarning(jex,
                "[{ReqId}] SimulationCanary: non-JSON response from={Source} to={Sink}",
                item.ReqId, item.Source, item.Sink);
            SimulationTotal.WithLabels("rpc_parse_error", prefixLabel).Inc();
            return;
        }

        int expected = unwrapCalls.Count + 1;
        var parsed = ParseSimulateV1Response(json, expected);

        switch (parsed.Outcome)
        {
            case BundleOutcome.TopLevelError:
            case BundleOutcome.MethodNotSupported:
                var resultLabel = parsed.Outcome == BundleOutcome.MethodNotSupported
                    ? "rpc_method_not_supported" : "rpc_error";
                _log.LogWarning(
                    "[{ReqId}] SimulationCanary: eth_simulateV1 returned error code={Code} message={Msg} from={Source} to={Sink}",
                    item.ReqId, parsed.ErrorCode, parsed.RevertMessage ?? "unknown", item.Source, item.Sink);
                SimulationTotal.WithLabels(resultLabel, prefixLabel).Inc();
                return;

            case BundleOutcome.EmptyResponse:
            case BundleOutcome.MissingCallsArray:
                _log.LogWarning(
                    "[{ReqId}] SimulationCanary: eth_simulateV1 returned no usable result from={Source} to={Sink}",
                    item.ReqId, item.Source, item.Sink);
                SimulationTotal.WithLabels("rpc_empty_response", prefixLabel).Inc();
                return;

            case BundleOutcome.Truncated:
                // Fewer results than calls sent. Fail closed — never silently classify
                // missing entries as success.
                _log.LogWarning(
                    "[{ReqId}] SimulationCanary: eth_simulateV1 returned truncated results, expected {Expected} — treating as truncated",
                    item.ReqId, expected);
                SimulationTotal.WithLabels("rpc_truncated_response", prefixLabel).Inc();
                return;

            case BundleOutcome.Revert:
                SimulationTotal.WithLabels("revert", prefixLabel).Inc();
                SimulationRevertTotal.WithLabels(parsed.Category!, parsed.Label!, parsed.Stage!).Inc();
                // Full replay context: everything needed to reproduce, incl. calldata
                // and the wrapper list so the canary log alone is sufficient to feed
                // scripts/_resources/run-sim.sh.
                var wrappers = string.Join(",", unwrapCalls.Select(u => u.Wrapper));
                _log.LogError(
                    "[{ReqId}] SimulationCanary: REVERT stage={Stage} category={Category} label={Label} " +
                    "from={Source} to={Sink} graphBlock={Block} simBlock={SimBlock} steps={Steps} unwraps={Unwraps} " +
                    "revert={Revert} wrappers={Wrappers} calldata={Calldata}",
                    item.ReqId, parsed.Stage, parsed.Category, parsed.Label,
                    item.Source, item.Sink, item.GraphBlock, blockTag,
                    item.Transfers.Count, unwrapCalls.Count,
                    parsed.RevertMessage ?? parsed.RevertData, wrappers, flowMatrixCalldata);
                return;

            case BundleOutcome.Success:
                SimulationTotal.WithLabels("success", prefixLabel).Inc();
                return;
        }
    }

    /// <summary>
    /// Discriminated outcome of an eth_simulateV1 bundle. Each value maps to exactly one
    /// metric label so the canary cannot silently fold a partial response into success.
    /// </summary>
    internal enum BundleOutcome
    {
        Success,
        TopLevelError,
        MethodNotSupported,
        EmptyResponse,
        MissingCallsArray,
        Truncated,
        Revert
    }

    internal readonly record struct BundleParseResult(
        BundleOutcome Outcome,
        int ErrorCode = 0,
        string? Stage = null,
        string? Category = null,
        string? Label = null,
        string? RevertData = null,
        string? RevertMessage = null);

    /// <summary>
    /// Pure parser for eth_simulateV1 responses, isolated so the truncation and
    /// status-classification paths can be unit-tested without HTTP plumbing.
    /// </summary>
    internal static BundleParseResult ParseSimulateV1Response(JsonElement json, int expectedCalls)
    {
        // Top-level JSON-RPC error (method not supported, validation failed, etc.).
        if (json.TryGetProperty("error", out var topError))
        {
            int code = 0;
            if (topError.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number)
                c.TryGetInt32(out code);
            var msg = topError.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
            var outcome = code == -32601 ? BundleOutcome.MethodNotSupported : BundleOutcome.TopLevelError;
            return new BundleParseResult(outcome, ErrorCode: code, RevertMessage: msg);
        }

        if (!json.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array
            || result.GetArrayLength() == 0)
            return new BundleParseResult(BundleOutcome.EmptyResponse);

        // eth_simulateV1 returns one entry per blockStateCalls element; we sent exactly one.
        var block0 = result[0];
        if (!block0.TryGetProperty("calls", out var innerCalls) || innerCalls.ValueKind != JsonValueKind.Array)
            return new BundleParseResult(BundleOutcome.MissingCallsArray);

        int actual = innerCalls.GetArrayLength();
        if (actual < expectedCalls)
            return new BundleParseResult(BundleOutcome.Truncated);

        // Scan unwrap results in order; surface the FIRST failing one.
        // Extra entries beyond expectedCalls are ignored.
        for (int i = 0; i < expectedCalls; i++)
        {
            var call = innerCalls[i];
            bool isFlowMatrix = i == expectedCalls - 1;
            string stage = isFlowMatrix ? "flow_matrix" : $"unwrap_{i}";

            var status = call.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status == "0x1")
                continue;

            string? revertData = null;
            string? revertMsg = null;
            if (call.TryGetProperty("error", out var callErr))
            {
                if (callErr.TryGetProperty("data", out var d)) revertData = d.GetString();
                if (callErr.TryGetProperty("message", out var m)) revertMsg = m.GetString();
            }
            // Fallback: some nodes put returnData on the failing call instead of error.
            if (revertData == null && call.TryGetProperty("returnData", out var rd))
                revertData = rd.GetString();

            var (category, label) = RevertClassifier.Classify(revertData ?? revertMsg ?? "unknown");
            return new BundleParseResult(
                BundleOutcome.Revert,
                Stage: stage,
                Category: category,
                Label: label,
                RevertData: revertData,
                RevertMessage: revertMsg);
        }

        return new BundleParseResult(BundleOutcome.Success);
    }

    /// <summary>
    /// Replaces wrapper contract addresses in TokenOwner fields with the underlying avatar address.
    /// Hub.sol's operateFlowMatrix expects avatar addresses (circlesId), not wrapper contracts.
    /// Returns the original list unchanged if no mapping is provided or no wrappers are found.
    /// </summary>
    private static IReadOnlyList<TransferPathStep> ResolveWrapperTokenOwners(
        List<TransferPathStep> transfers,
        IReadOnlyDictionary<string, string>? wrapperToAvatar)
    {
        if (wrapperToAvatar == null || wrapperToAvatar.Count == 0)
            return transfers;

        var resolved = new List<TransferPathStep>(transfers.Count);
        foreach (var t in transfers)
        {
            var tokenOwner = t.TokenOwner.ToLowerInvariant();
            if (wrapperToAvatar.TryGetValue(tokenOwner, out var avatar))
            {
                resolved.Add(new TransferPathStep
                {
                    From = t.From,
                    To = t.To,
                    TokenOwner = avatar,
                    Value = t.Value
                });
            }
            else
            {
                resolved.Add(t);
            }
        }

        return resolved;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Inflationary unit conversion for InflationaryCircles wrappers.
    //
    // Circles V2 has two ERC20 wrapper flavors that BuildUnwrapPrefix cannot
    // distinguish from the transfer list alone — both expose `unwrap(uint256)`
    // but interpret the argument differently:
    //
    //   - DemurrageCircles  (circlesType=0, symbol `CRC`/`gCRC`):
    //       unwrap(_amount) burns _amount ERC20 AND transfers _amount of 1155
    //       — argument is in demurraged 1155 units (1:1 with the underlying).
    //       BuildUnwrapPrefix's sum (in demurraged units) is correct as-is.
    //
    //   - InflationaryCircles (circlesType=1, symbol `s-`-prefixed):
    //       unwrap(_amount) burns _amount ERC20 and transfers `_amount * γ^day`
    //       of 1155 — argument is in inflationary ERC20 units. To release D of
    //       1155 we must call unwrap(D * β^day) = unwrap(convertDemurrageToInflationaryValue(D, day)).
    //
    // Both wrappers inherit `convertDemurrageToInflationaryValue` from the
    // shared Demurrage base, so the function ALWAYS applies β^day regardless of
    // wrapper flavor. We branch on InflationaryWrappers set membership, not on
    // the conversion function's return value.
    //
    // Verified on-chain 2026-05-27 via direct eth_simulateV1 probes against
    // wrapper 0x548c20e6 (gCRC, demurraged) and 0x5d7eaaed (s-gCRC, inflationary):
    //   - DemurrageCircles.unwrap(B)  → 1155 minted = B            (ratio 1.0)
    //   - InflationaryCircles.unwrap(B) → 1155 minted = B * γ^day  (ratio β^day)
    // ──────────────────────────────────────────────────────────────────────

    private const string ConvertDemurrageToInflationarySelector = "0x253dd0b5";
    private const long InflationDayZeroSeconds = (long)DemurrageCalculator.InflationDayZeroUnix;
    private const long SecondsPerDay = 86_400L;
    private const string ZeroSenderForReadOnly = "0x0000000000000000000000000000000000000000";

    /// <summary>
    /// Day index for Circles V2 demurrage math. Mirrors the on-chain
    /// <c>Demurrage.day(blockTimestamp) = (blockTimestamp - inflationDayZero) / 86400</c>.
    /// Pre-genesis or negative inputs yield day = 0, never a negative value.
    /// </summary>
    internal static long ComputeInflationDay(long blockTimestampSeconds)
    {
        long delta = blockTimestampSeconds - InflationDayZeroSeconds;
        return delta <= 0 ? 0 : delta / SecondsPerDay;
    }

    /// <summary>
    /// Encodes calldata for <c>convertDemurrageToInflationaryValue(uint256,uint64)</c>.
    /// </summary>
    internal static string EncodeConvertDemurrageCalldata(BigInteger demurragedAmount, long day)
    {
        if (demurragedAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(demurragedAmount), "uint256 amount must be non-negative");
        if (day < 0)
            throw new ArgumentOutOfRangeException(nameof(day), "day must be non-negative");

        // BigInteger.ToString("x") prepends a leading 0 on positive numbers whose top
        // nibble is ≥ 0x8 to avoid sign ambiguity. Trim it so the left-pad math is correct
        // (same rationale as EncodeUnwrapCalldata).
        var amountHex = demurragedAmount.ToString("x", CultureInfo.InvariantCulture);
        if (amountHex.Length > 0 && amountHex[0] == '0' && amountHex.Length > 1) amountHex = amountHex.TrimStart('0');
        if (amountHex.Length == 0) amountHex = "0";

        // `day` is a long (>= 0 guarded above); long.ToString("x") emits minimal hex
        // for positive values with NO sign-disambiguating leading zero, unlike BigInteger.
        var dayHex = day.ToString("x", CultureInfo.InvariantCulture);

        return ConvertDemurrageToInflationarySelector
             + amountHex.PadLeft(64, '0')
             + dayHex.PadLeft(64, '0');
    }

    /// <summary>
    /// Parses one eth_simulateV1 call's returnData into a non-negative BigInteger.
    /// Returns null if the call did not succeed, the payload is empty, or the hex
    /// does not decode.
    /// </summary>
    internal static BigInteger? ParseConvertCallReturnData(JsonElement call)
    {
        if (!call.TryGetProperty("status", out var status) || status.GetString() != "0x1")
            return null;
        if (!call.TryGetProperty("returnData", out var rd))
            return null;
        var hex = rd.GetString();
        if (string.IsNullOrEmpty(hex) || hex == "0x")
            return null;
        if (hex.StartsWith("0x", StringComparison.Ordinal)) hex = hex[2..];
        // Prepend a leading '0' so BigInteger treats the value as unsigned even if
        // the top nibble is ≥ 0x8 (e.g., a uint256 with the high bit set).
        return BigInteger.TryParse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    /// <summary>
    /// Extracts the per-call resolved amount from a batched eth_simulateV1 response.
    /// Always returns a list of length <paramref name="expectedCount"/>; failed calls yield null.
    /// </summary>
    internal static IReadOnlyList<BigInteger?> ExtractInflationaryAmounts(JsonElement json, int expectedCount)
    {
        var amounts = new BigInteger?[expectedCount];
        if (!json.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
            return amounts;
        var block0 = result[0];
        if (!block0.TryGetProperty("calls", out var innerCalls) || innerCalls.ValueKind != JsonValueKind.Array)
            return amounts;
        int actual = innerCalls.GetArrayLength();
        for (int i = 0; i < expectedCount && i < actual; i++)
            amounts[i] = ParseConvertCallReturnData(innerCalls[i]);
        return amounts;
    }

    /// <summary>
    /// Applies resolved inflationary amounts to the original unwrap-call list. Only
    /// entries with <c>IsInflationary=true</c> consume the resolver's output; demurraged
    /// entries pass through unchanged. A null resolved amount (RPC/parse failure) falls
    /// back to the demurraged amount — the canary still attempts a simulation, which
    /// just produces the prior false-positive class for that one inflationary wrapper,
    /// not silently skipping coverage.
    /// </summary>
    internal static IReadOnlyList<UnwrapCall> ApplyInflationaryAmounts(
        IReadOnlyList<UnwrapCall> calls,
        IReadOnlyList<BigInteger?> inflationaryAmountsForInflationaryCalls)
    {
        var resolved = new List<UnwrapCall>(calls.Count);
        int infIdx = 0;
        for (int i = 0; i < calls.Count; i++)
        {
            var src = calls[i];
            if (!src.IsInflationary)
            {
                resolved.Add(src);
                continue;
            }
            BigInteger? resolvedAmount = infIdx < inflationaryAmountsForInflationaryCalls.Count
                ? inflationaryAmountsForInflationaryCalls[infIdx]
                : null;
            infIdx++;
            resolved.Add(new UnwrapCall(src.From, src.Wrapper, resolvedAmount ?? src.Amount, src.IsInflationary));
        }
        return resolved;
    }

    /// <summary>
    /// Fetches the block timestamp (seconds since unix epoch) for a JSON-RPC block tag.
    /// Returns null on any failure; caller decides fallback strategy. Every non-success
    /// branch logs a warning for observability; metric label cardinality kept low via
    /// the single <c>block_lookup_failed</c> label emitted by the caller.
    /// </summary>
    private async Task<long?> FetchBlockTimestampAsync(string blockTag, HttpClient client, CancellationToken ct)
    {
        var rpc = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "eth_getBlockByNumber",
            @params = new object[] { blockTag, false }
        };
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(_rpcUrl, rpc, ct);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "SimulationCanary: FetchBlockTimestamp: eth_getBlockByNumber failed (network/timeout) for {Block}",
                blockTag);
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "SimulationCanary: FetchBlockTimestamp: eth_getBlockByNumber HTTP {StatusCode} for {Block}",
                (int)response.StatusCode, blockTag);
            return null;
        }
        JsonElement json;
        try { json = await response.Content.ReadFromJsonAsync<JsonElement>(ct); }
        catch (JsonException jex)
        {
            _log.LogWarning(jex,
                "SimulationCanary: FetchBlockTimestamp: non-JSON response for {Block}", blockTag);
            return null;
        }
        if (json.TryGetProperty("error", out var rpcError))
        {
            var msg = rpcError.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
            _log.LogWarning(
                "SimulationCanary: FetchBlockTimestamp: JSON-RPC error for {Block}: {Msg}",
                blockTag, msg);
            return null;
        }
        if (!json.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            _log.LogWarning(
                "SimulationCanary: FetchBlockTimestamp: missing/non-object result for {Block}", blockTag);
            return null;
        }
        if (!result.TryGetProperty("timestamp", out var tsProp))
        {
            _log.LogWarning(
                "SimulationCanary: FetchBlockTimestamp: missing timestamp field for {Block}", blockTag);
            return null;
        }
        var hex = tsProp.GetString();
        if (string.IsNullOrEmpty(hex))
        {
            _log.LogWarning(
                "SimulationCanary: FetchBlockTimestamp: null/empty timestamp hex for {Block}", blockTag);
            return null;
        }
        if (hex.StartsWith("0x", StringComparison.Ordinal)) hex = hex[2..];
        try { return Convert.ToInt64(hex, 16); }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "SimulationCanary: FetchBlockTimestamp: timestamp parse failed for {Block} hex={Hex}", blockTag, hex);
            return null;
        }
    }

    /// <summary>
    /// For each unwrap call against an InflationaryCircles wrapper, replaces its
    /// demurraged amount with the inflationary-unit equivalent at the simulation block's
    /// day index, by batch-calling <c>convertDemurrageToInflationaryValue(amount, day)</c>
    /// via eth_simulateV1 at the same block tag.
    /// <para>
    /// Failures (block lookup, RPC, parse) fall back to the demurraged amount per-call —
    /// the bundle still simulates, which may produce the prior false-positive class
    /// (<c>ERC1155InsufficientBalance</c> for inflationary unwraps) but never widens
    /// coverage loss. Per-failure-class metrics use the existing simulation_total counter
    /// with new result labels (<c>block_lookup_failed</c>, <c>inflation_resolve_failed</c>).
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<UnwrapCall>> ResolveInflationaryAmountsAsync(
        CanaryWorkItem item,
        IReadOnlyList<UnwrapCall> calls,
        string blockTag,
        HttpClient client,
        CancellationToken ct)
    {
        // Step 1: pull block timestamp (one RPC, deterministic key for the conversion math).
        var ts = await FetchBlockTimestampAsync(blockTag, client, ct);
        if (ts == null)
        {
            SimulationTotal.WithLabels("block_lookup_failed", "unwrap").Inc();
            // Fall through: simulate bundle with demurraged amounts. For inflationary wrappers
            // this reverts at unwrap() (the original f72f2d61-class FP), but it preserves the
            // pre-PR signal — strictly better than silently dropping the canary attempt.
            return calls;
        }
        long day = ComputeInflationDay(ts.Value);

        // Step 2: collect inflationary-only positions; their order is preserved across the
        // batched call array so ExtractInflationaryAmounts indexes match.
        var inflationaryIndices = new List<int>();
        for (int i = 0; i < calls.Count; i++)
            if (calls[i].IsInflationary) inflationaryIndices.Add(i);

        if (inflationaryIndices.Count == 0) return calls;

        // Step 3: batch eth_simulateV1 — one call per inflationary unwrap.
        // `from = ZERO_ADDRESS` is fine: convertDemurrageToInflationaryValue is a pure view
        // function on the wrapper, ignores msg.sender.
        var batchCalls = new List<object>(inflationaryIndices.Count);
        foreach (var idx in inflationaryIndices)
        {
            var c = calls[idx];
            batchCalls.Add(new
            {
                from = ZeroSenderForReadOnly,
                to = c.Wrapper,
                data = EncodeConvertDemurrageCalldata(c.Amount, day)
            });
        }
        var rpc = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "eth_simulateV1",
            @params = new object[]
            {
                new
                {
                    blockStateCalls = new[] { new { calls = batchCalls } },
                    validation = false,
                    traceTransfers = false
                },
                blockTag
            }
        };
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(_rpcUrl, rpc, ct);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[{ReqId}] SimulationCanary: ResolveInflationaryAmounts: eth_simulateV1 failed (network/timeout)",
                item.ReqId);
            SimulationTotal.WithLabels("inflation_resolve_failed", "unwrap").Inc();
            return calls;
        }
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "[{ReqId}] SimulationCanary: ResolveInflationaryAmounts: eth_simulateV1 HTTP {StatusCode}",
                item.ReqId, (int)response.StatusCode);
            SimulationTotal.WithLabels("inflation_resolve_failed", "unwrap").Inc();
            return calls;
        }
        JsonElement json;
        try { json = await response.Content.ReadFromJsonAsync<JsonElement>(ct); }
        catch (JsonException jex)
        {
            _log.LogWarning(jex,
                "[{ReqId}] SimulationCanary: ResolveInflationaryAmounts: non-JSON response",
                item.ReqId);
            SimulationTotal.WithLabels("inflation_resolve_failed", "unwrap").Inc();
            return calls;
        }

        // Step 4: extract per-position results; ApplyInflationaryAmounts substitutes only
        // inflationary positions (demurraged calls pass through unchanged).
        var inflationaryAmounts = ExtractInflationaryAmounts(json, inflationaryIndices.Count);
        return ApplyInflationaryAmounts(calls, inflationaryAmounts);
    }
}
