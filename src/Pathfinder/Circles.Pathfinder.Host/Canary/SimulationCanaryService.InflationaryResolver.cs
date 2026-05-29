using System.Net.Http.Json;
using System.Text.Json;
using Circles.Common;

namespace Circles.Pathfinder.Host.Canary;

/// <summary>
/// Block-timestamp lookup and batched inflationary-unit resolver for the canary.
/// Sources the day index for the simulation block, batches one
/// <c>convertDemurrageToInflationaryValue</c> call per InflationaryCircles wrapper via
/// eth_simulateV1, and falls back to the demurraged amount per-call on resolver failure so
/// coverage is never silently dropped.
/// </summary>
internal sealed partial class SimulationCanaryService
{
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
    internal async Task<IReadOnlyList<ResolvedUnwrapCall>> ResolveInflationaryAmountsAsync(
        CanaryWorkItem item,
        IReadOnlyList<DemurragedUnwrapCall> calls,
        string blockTag,
        HttpClient client,
        CancellationToken ct)
    {
        // Step 0: fast path — if no entries need conversion, promote 1:1 with zero RPC overhead.
        // The common case (DemurrageCircles only, ≈89% of wrapper population) hits this branch.
        var inflationaryIndices = new List<int>();
        for (int i = 0; i < calls.Count; i++)
            if (calls[i].WrapperType == CirclesType.InflationaryCircles) inflationaryIndices.Add(i);

        if (inflationaryIndices.Count == 0) return PromoteAllDemurraged(calls);

        // Step 1: pull block timestamp (one RPC, deterministic key for the conversion math).
        var ts = await FetchBlockTimestampAsync(blockTag, client, ct);
        if (ts == null)
        {
            SimulationTotal.WithLabels("block_lookup_failed", "unwrap").Inc();
            // Fall through: simulate bundle with demurraged amounts. For inflationary wrappers
            // this reverts at unwrap() (the original f72f2d61-class FP), but it preserves the
            // pre-PR signal — strictly better than silently dropping the canary attempt.
            return PromoteAllDemurraged(calls);
        }
        long day = ComputeInflationDay(ts.Value);

        // Step 2: batch eth_simulateV1 — one call per inflationary unwrap.
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
                data = EncodeConvertDemurrageCalldata(c.DemurragedAmount, day)
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
            return PromoteAllDemurraged(calls);
        }
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "[{ReqId}] SimulationCanary: ResolveInflationaryAmounts: eth_simulateV1 HTTP {StatusCode}",
                item.ReqId, (int)response.StatusCode);
            SimulationTotal.WithLabels("inflation_resolve_failed", "unwrap").Inc();
            return PromoteAllDemurraged(calls);
        }
        JsonElement json;
        try { json = await response.Content.ReadFromJsonAsync<JsonElement>(ct); }
        catch (JsonException jex)
        {
            _log.LogWarning(jex,
                "[{ReqId}] SimulationCanary: ResolveInflationaryAmounts: non-JSON response",
                item.ReqId);
            SimulationTotal.WithLabels("inflation_resolve_failed", "unwrap").Inc();
            return PromoteAllDemurraged(calls);
        }

        // Step 3.5: top-level RPC error short-circuit. When the JSON-RPC layer
        // itself rejects the batch (parse error, method not found, internal error
        // upstream), the response carries `{"error": {"code": …, "message": …}}`
        // at the root with no `result` field. Without this check ExtractInflationaryAmounts
        // would log one per-call partial-failure warning per inflationary entry and
        // emit N `inflation_resolve_partial` counter ticks — noisy and misleading,
        // since the root cause is a single batch rejection, not N independent failures.
        // `"error": null` is a valid JSON-RPC success-shape emitted by some servers
        // alongside a populated `result` — guard explicitly so we don't false-positive
        // on it and trigger the per-batch fallback when the batch actually succeeded.
        if (json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("error", out var rpcErr)
            && rpcErr.ValueKind != JsonValueKind.Null)
        {
            var errMsg = rpcErr.ValueKind == JsonValueKind.Object && rpcErr.TryGetProperty("message", out var m)
                ? m.GetString()
                : rpcErr.ToString();
            _log.LogWarning(
                "[{ReqId}] SimulationCanary: ResolveInflationaryAmounts: eth_simulateV1 batch rejected at RPC layer: {Error}",
                item.ReqId, errMsg);
            SimulationTotal.WithLabels("inflation_resolve_failed", "unwrap").Inc();
            return PromoteAllDemurraged(calls);
        }

        // Step 4: extract per-position results; ApplyInflationaryAmounts substitutes only
        // inflationary positions (demurraged calls pass through unchanged).
        var inflationaryAmounts = ExtractInflationaryAmounts(json, inflationaryIndices.Count);

        // Per-call partial-failure observability: a null entry in `inflationaryAmounts`
        // means the convert call for that wrapper failed (status != 0x1, empty returnData,
        // or unparseable hex). ApplyInflationaryAmounts will fall back to the demurraged
        // amount, which then drives `unwrap()` and reverts with ERC1155InsufficientBalance —
        // the exact f72f2d61-class FP this code is trying to suppress. Without per-wrapper
        // observability operators can't distinguish "real on-chain bug" from "resolver
        // partial failure that re-introduced the prior FP class for one wrapper".
        for (int i = 0; i < inflationaryAmounts.Count; i++)
        {
            if (inflationaryAmounts[i] != null) continue;
            var call = calls[inflationaryIndices[i]];
            _log.LogWarning(
                "[{ReqId}] SimulationCanary: ResolveInflationaryAmounts: per-call resolve failed for wrapper={Wrapper} from={From} — falling back to demurraged (will likely revert at unwrap with ERC1155InsufficientBalance)",
                item.ReqId, call.Wrapper, call.From);
            SimulationTotal.WithLabels("inflation_resolve_partial", "unwrap").Inc();
        }

        return ApplyInflationaryAmounts(calls, inflationaryAmounts, _log, item.ReqId);
    }
}
