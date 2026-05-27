using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using System.Threading.Channels;
using Circles.Common.Dto;
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
    bool WithWrap = false);

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
                ? BuildUnwrapPrefix(item.Transfers, item.WrapperToAvatar)
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
                // BuildUnwrapPrefix sums in demurraged 1155 units; InflationaryCircles
                // wrappers store ERC20 supply in inflation-corrected units, so the same
                // numeric value yields fewer 1155 tokens after unwrap. Pre-resolve every
                // unwrap to the wrapper's native unit before assembling the bundle.
                long? blockTimestamp = await FetchBlockTimestampAsync(blockTag, client, ct);
                if (!blockTimestamp.HasValue)
                {
                    _log.LogWarning(
                        "[{ReqId}] SimulationCanary: failed to fetch block timestamp for {Block}; skipping unwrap-prefix simulation",
                        item.ReqId, blockTag);
                    SimulationTotal.WithLabels("block_lookup_failed", prefixLabel).Inc();
                    return;
                }
                long day = ComputeInflationDay(blockTimestamp.Value);
                var resolvedUnwraps = await ResolveInflationaryAmountsAsync(unwrapCalls, day, blockTag, client, ct);

                await SimulateBundleAsync(item, resolvedUnwraps, calldata, blockTag, prefixLabel, client, ct);
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
    /// </summary>
    internal readonly record struct UnwrapCall(string From, string Wrapper, BigInteger Amount);

    /// <summary>
    /// For each transfer whose TokenOwner is a known wrapper, attribute one unwrap call
    /// to the transfer's From address for the full transfer value. Groups identical
    /// (from, wrapper) pairs and sums amounts so a single SDK-equivalent unwrap precedes
    /// the operateFlowMatrix call in the bundle.
    /// </summary>
    internal static IReadOnlyList<UnwrapCall> BuildUnwrapPrefix(
        List<TransferPathStep> transfers,
        IReadOnlyDictionary<string, string>? wrapperToAvatar)
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
            calls.Add(new UnwrapCall(key.From, key.Wrapper, sums[key]));

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

    // --- Inflationary-units conversion ---
    //
    // Circles V2 wrappers come in two storage flavors that BuildUnwrapPrefix cannot
    // distinguish from the transfer list alone:
    //   - DemurrageCircles: ERC20 supply tracked in demurraged units. unwrap(x) burns x
    //     of the ERC20 and credits ~x of the underlying 1155 (1:1, modulo today's drift).
    //   - InflationaryCircles ("s-" symbol prefix): ERC20 supply tracked in raw,
    //     inflation-corrected units. unwrap(x) burns x of the ERC20 and credits LESS
    //     than x of the 1155 (the demurraged equivalent at today's day index).
    //
    // BuildUnwrapPrefix sums transfer values in demurraged units (Hub 1155 semantics),
    // so passing that sum directly to an InflationaryCircles wrapper under-credits the
    // holder and the subsequent operateFlowMatrix reverts with InsufficientBalance —
    // a canary false positive, because the SDK's real broadcast path unwraps in the
    // wrapper's native unit. Convert per-(from, wrapper) once before bundle assembly.
    //
    // The conversion lives on the wrapper itself:
    //   convertDemurrageToInflationaryValue(uint256 amount, uint64 day) → uint256
    // For DemurrageCircles wrappers the call returns its input (identity), so the
    // resolution is type-agnostic at the call site.
    private const string ConvertDemurrageToInflationarySelector = "0x253dd0b5";
    private const long InflationDayZeroSeconds = 1602720000L; // Circles V2 inflationDayZero
    private const long SecondsPerDay = 86400L;
    private const string ZeroSenderForReadOnly = "0x0000000000000000000000000000000000000000";

    /// <summary>
    /// Day index used by Circles V2 demurrage math. Mirrors the on-chain
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

        var amountHex = demurragedAmount.ToString("x", CultureInfo.InvariantCulture);
        if (amountHex.Length > 0 && amountHex[0] == '0' && amountHex.Length > 1) amountHex = amountHex.TrimStart('0');
        if (amountHex.Length == 0) amountHex = "0";

        var dayHex = day.ToString("x", CultureInfo.InvariantCulture);

        return ConvertDemurrageToInflationarySelector
             + amountHex.PadLeft(64, '0')
             + dayHex.PadLeft(64, '0');
    }

    /// <summary>
    /// Parses the returnData hex of a single eth_simulateV1 call result into a
    /// non-negative BigInteger. Returns null if the call did not succeed, the
    /// payload is empty, or the hex does not decode.
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
    /// Builds the resolved unwrap-call list given the original demurraged-unit calls
    /// and the inflationary amounts parsed from a batched eth_simulateV1 response.
    /// Position i in <paramref name="inflationaryAmounts"/> corresponds to position i
    /// in <paramref name="demurraged"/>. A null entry (parse/call failed) falls back
    /// to the demurraged amount so the canary still attempts a simulation rather than
    /// failing closed on a single quirky wrapper.
    /// </summary>
    internal static IReadOnlyList<UnwrapCall> ApplyInflationaryAmounts(
        IReadOnlyList<UnwrapCall> demurraged,
        IReadOnlyList<BigInteger?> inflationaryAmounts)
    {
        if (demurraged.Count != inflationaryAmounts.Count)
            throw new ArgumentException(
                $"Length mismatch: {demurraged.Count} unwrap calls vs {inflationaryAmounts.Count} resolved amounts",
                nameof(inflationaryAmounts));

        var resolved = new List<UnwrapCall>(demurraged.Count);
        for (int i = 0; i < demurraged.Count; i++)
        {
            var src = demurraged[i];
            var amt = inflationaryAmounts[i] ?? src.Amount;
            resolved.Add(new UnwrapCall(src.From, src.Wrapper, amt));
        }
        return resolved;
    }

    /// <summary>
    /// Fetches the block timestamp (seconds since unix epoch) for a JSON-RPC block tag
    /// (hex block number or "latest"). Returns null if the RPC call fails or the
    /// response shape is unexpected; caller decides whether to fall back or abort.
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
        catch (Exception)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode) return null;

        JsonElement json;
        try { json = await response.Content.ReadFromJsonAsync<JsonElement>(ct); }
        catch (JsonException) { return null; }

        if (!json.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return null;
        if (!result.TryGetProperty("timestamp", out var tsProp))
            return null;
        var hex = tsProp.GetString();
        if (string.IsNullOrEmpty(hex)) return null;
        if (hex.StartsWith("0x", StringComparison.Ordinal)) hex = hex[2..];
        try { return Convert.ToInt64(hex, 16); }
        catch { return null; }
    }

    /// <summary>
    /// Resolves each (from, wrapper, demurraged) unwrap call to its inflationary
    /// equivalent via a single batched eth_simulateV1 read against the same block
    /// the bundle will run at. Per-call failures fall back to the demurraged amount.
    /// </summary>
    private async Task<IReadOnlyList<UnwrapCall>> ResolveInflationaryAmountsAsync(
        IReadOnlyList<UnwrapCall> unwrapCalls,
        long day,
        string blockTag,
        HttpClient client,
        CancellationToken ct)
    {
        if (unwrapCalls.Count == 0) return unwrapCalls;

        var calls = new List<object>(unwrapCalls.Count);
        foreach (var u in unwrapCalls)
        {
            calls.Add(new
            {
                from = ZeroSenderForReadOnly,
                to = u.Wrapper,
                data = EncodeConvertDemurrageCalldata(u.Amount, day)
            });
        }

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
        catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return unwrapCalls; }

        if (!response.IsSuccessStatusCode) return unwrapCalls;

        JsonElement json;
        try { json = await response.Content.ReadFromJsonAsync<JsonElement>(ct); }
        catch (JsonException) { return unwrapCalls; }

        var amounts = ExtractInflationaryAmounts(json, unwrapCalls.Count);
        return ApplyInflationaryAmounts(unwrapCalls, amounts);
    }

    /// <summary>
    /// Pulls the per-call inflationary amounts from an eth_simulateV1 response.
    /// Always returns a list whose length equals <paramref name="expectedCount"/>;
    /// entries that could not be parsed (top-level error, truncated array, reverted
    /// call) are surfaced as null so the caller can fall back to demurraged units.
    /// </summary>
    internal static IReadOnlyList<BigInteger?> ExtractInflationaryAmounts(JsonElement json, int expectedCount)
    {
        var slots = new BigInteger?[expectedCount];
        if (json.TryGetProperty("error", out _)) return slots;
        if (!json.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array
            || result.GetArrayLength() == 0)
            return slots;

        var block0 = result[0];
        if (!block0.TryGetProperty("calls", out var innerCalls) || innerCalls.ValueKind != JsonValueKind.Array)
            return slots;

        int available = Math.Min(innerCalls.GetArrayLength(), expectedCount);
        for (int i = 0; i < available; i++)
            slots[i] = ParseConvertCallReturnData(innerCalls[i]);
        return slots;
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
}
