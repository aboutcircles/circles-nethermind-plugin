using System.Net.Http.Json;
using System.Text.Json;
using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Simulation;

namespace Circles.Pathfinder.Host.Canary;

/// <summary>
/// eth_simulateV1 bundle assembly and response parsing for the unwrap-prefix path.
/// Maps each bundle outcome to exactly one metric label so partial responses cannot be
/// silently folded into success. Also hosts the wrapper→avatar TokenOwner rewrite that
/// makes the operateFlowMatrix calldata target avatar addresses, not wrapper contracts.
/// </summary>
internal sealed partial class SimulationCanaryService
{
    /// <summary>
    /// Executes the unwrap-prefix bundle via Nethermind's eth_simulateV1. Sequences
    /// the unwrap calls then the operateFlowMatrix call; state is shared across calls
    /// within a single blockStateCalls entry, mirroring the SDK's actual broadcast order.
    /// </summary>
    private async Task SimulateBundleAsync(
        CanaryWorkItem item,
        IReadOnlyList<ResolvedUnwrapCall> unwrapCalls,
        string flowMatrixCalldata,
        string blockTag,
        string prefixLabel,
        HttpClient client,
        CancellationToken ct)
    {
        var calls = new List<object>(unwrapCalls.Count + 1);
        foreach (var u in unwrapCalls)
        {
            // Amount is in the wrapper's native unwrap-argument unit by construction —
            // the ResolvedUnwrapCall type makes that invariant compile-enforced.
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

                // #74 cache-balance-drift: the bundle path classifies insufficient_balance
                // identically to the plain eth_call path and, being wrapped/ScoreGroup-token-tied,
                // is the most likely carrier of the signal. Decode from the SAME source Classify
                // used (revertData ?? revertMsg) — see ParseSimulateV1Response.
                var driftEmitted = EmitBalanceDriftIfDetected(item, parsed.Label!, parsed.RevertData ?? parsed.RevertMessage);

                // Fall back to the active probe when the payload didn't explain the revert (e.g. the
                // unclassified 0x66ef7607 class). Skip category=simulation (valid-path canary
                // artifacts, no balance shortfall). Replay this bundle's unwrap prefix so balanceOf
                // reflects post-unwrap state — see ProbeBalanceDriftAsync.
                if (!driftEmitted && BalanceProbeEnabled && parsed.Category != "simulation")
                    await ProbeBalanceDriftAsync(item, unwrapCalls, blockTag, client, ct);
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
