using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using Circles.Common.Dto;
using Circles.Pathfinder.Simulation;

namespace Circles.Pathfinder.Host.Canary;

/// <summary>
/// Active cache-balance-drift probe (#74), the selector-agnostic complement to the payload-based
/// detector in <see cref="EmitBalanceDriftIfDetected"/>.
///
/// <para>The payload detector only fires on <c>ERC1155InsufficientBalance</c> (<c>0x03dee4c5</c>):
/// it reads the holder/balance/needed straight out of the revert args for free. But a phantom
/// (cache-inflated) balance can trip a DIFFERENT terminal error first — observed live as an
/// unclassified <c>0x66ef7607</c> revert routing through the same phantom-prone arb+group-token
/// pair — and those evade both the payload detector AND the Prometheus/Loki alerts
/// (<c>category=unknown</c> is not <c>category=bug</c>). This probe closes that gap: on any revert
/// the payload path did not already explain, it compares each holder's NET required outflow per
/// token (outflow minus the in-path inflow it receives of that token) against the holder's REAL
/// on-chain balance, and emits the same <c>CACHE BALANCE DRIFT</c> signal when the path demands
/// more than exists. Netting is essential: a group-mint router that forwards collateral it received
/// is a balanced pass-through (real balance legitimately zero), not a phantom — see
/// <see cref="AggregateRequiredOutflow"/>.</para>
///
/// <para><b>Soundness vs withWrap paths.</b> A naive <c>balanceOf</c> at graphBlock would
/// false-positive whenever the holder's supply comes from unwrapping wrapper ERC20 into 1155
/// (the balance only materialises after the unwrap). To avoid that — and because the Loki alert
/// fires on the log LINE regardless of metric bucket — the probe runs as a single
/// <c>eth_simulateV1</c> bundle that REPLAYS the same unwrap prefix the real simulation used and
/// THEN reads <c>balanceOf</c>. The returned balance is exactly the 1155 balance
/// <c>operateFlowMatrix</c> would see, so <c>required &gt; balanceOf</c> can only mean the served
/// graph sourced a balance no real on-chain state supports. The chain does all the
/// demurrage / unit / unwrap math; the probe does no unit conversion of its own.</para>
///
/// <para>Only ≥2× over-asks (and true zero-balance sends) are surfaced: a sub-2× overshoot is
/// indistinguishable from benign demurrage discretisation between the pathfinder's cached value
/// and the freshly-fetched chain value, whereas the phantom class is orders of magnitude
/// (~2834× observed). The metric reuses <see cref="BalanceDriftTotal"/> so existing alerts on
/// <c>{bucket}</c> keep working; holder/token stay in the log only (unbounded cardinality).</para>
/// </summary>
internal sealed partial class SimulationCanaryService
{
    // ERC1155 balanceOf(address account, uint256 id) — keccak256("balanceOf(address,uint256)")[:4].
    private const string Erc1155BalanceOfSelector = "0x00fdd58e";

    // Hard cap on distinct (holder, token) probes per revert. Reverts are rare (best-effort canary,
    // queue size 10) and distinct holders per path are typically < 10, but a pathological dense path
    // could enumerate many pairs — bound the eth_simulateV1 fan-out and log when we truncate so a
    // capped probe never reads as "checked everything".
    private const int MaxBalanceProbePairs = 64;

    // Default ON — this is the whole point of the hardening. Operators can disable with
    // CANARY_BALANCE_PROBE_ENABLED=false if the extra eth_simulateV1 per revert ever needs muting.
    private static readonly bool BalanceProbeEnabled =
        !string.Equals(
            Environment.GetEnvironmentVariable("CANARY_BALANCE_PROBE_ENABLED"),
            "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>One detected (holder, token) drift: the path needed <see cref="Needed"/> but the
    /// holder held only <see cref="OnChain"/> on-chain, bucketed by the needed/on-chain ratio.</summary>
    internal readonly record struct BalanceDriftRecord(
        string Holder, string Token, BigInteger OnChain, BigInteger Needed, string Bucket);

    /// <summary>
    /// Probes on-chain balances for each holder the path requires to send, after replaying the
    /// given unwrap prefix, and emits <c>CACHE BALANCE DRIFT</c> for any (holder, token) where the
    /// path's NET required outflow (outflow minus in-path inflow) exceeds the real balance by ≥2×
    /// (or the holder holds nothing).
    /// </summary>
    /// <param name="item">The work item whose path transfers are checked.</param>
    /// <param name="unwrapCalls">The unwrap prefix to replay before reading balances. Empty for the
    /// plain eth_call path (no wrapper supply); the resolved bundle list for the unwrap path.</param>
    /// <param name="blockTag">The block to simulate at (the graph block the path was built from).</param>
    /// <param name="client">HTTP client for the JSON-RPC call.</param>
    /// <param name="ct">Cancellation token (shutdown propagates).</param>
    /// <returns><c>true</c> if at least one drift was emitted; <c>false</c> otherwise.</returns>
    private async Task<bool> ProbeBalanceDriftAsync(
        CanaryWorkItem item,
        IReadOnlyList<ResolvedUnwrapCall> unwrapCalls,
        string blockTag,
        HttpClient client,
        CancellationToken ct)
    {
        try
        {
            // Net required outflow per (holder, token) from the avatar-resolved transfers (outflow
            // minus in-path inflow) — Hub.balanceOf is keyed by the avatar token id, not the wrapper.
            var transfers = ResolveWrapperTokenOwners(item.Transfers, item.WrapperToAvatar);
            var required = AggregateRequiredOutflow(transfers);
            if (required.Count == 0)
                return false;

            // Drop malformed addresses here rather than encoding a zero id-word for them: a zero word
            // makes balanceOf return 0, which would alias onto a spurious zero_balance "drift".
            var pairs = new List<(string Holder, string Token, BigInteger Required)>(required.Count);
            foreach (var ((holder, token), amount) in required)
                if (IsHexAddress(holder) && IsHexAddress(token))
                    pairs.Add((holder, token, amount));
            if (pairs.Count == 0)
                return false;

            if (pairs.Count > MaxBalanceProbePairs)
            {
                _log.LogWarning(
                    "[{ReqId}] SimulationCanary: balance probe capped at {Cap} of {Total} (holder,token) pairs — drift in the remainder is not checked",
                    item.ReqId, MaxBalanceProbePairs, pairs.Count);
                pairs = pairs.GetRange(0, MaxBalanceProbePairs);
            }

            // One eth_simulateV1: [ replayed unwrap prefix..., balanceOf(holder,token)... ].
            // Sharing one blockStateCalls entry means the balanceOf calls observe post-unwrap state,
            // exactly as operateFlowMatrix would in the real bundle.
            var calls = new List<object>(unwrapCalls.Count + pairs.Count);
            foreach (var u in unwrapCalls)
                calls.Add(new { from = u.From, to = u.Wrapper, data = EncodeUnwrapCalldata(u.Amount) });
            foreach (var p in pairs)
                calls.Add(new
                {
                    from = ZeroSenderForReadOnly,
                    to = FlowMatrixEncoder.CirclesHubAddress,
                    data = EncodeBalanceOfCalldata(p.Holder, p.Token)
                });

            var rpc = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "eth_simulateV1",
                @params = new object[]
                {
                    new { blockStateCalls = new[] { new { calls } }, validation = false, traceTransfers = false },
                    blockTag
                }
            };

            // Network failure and parse failure are distinct error classes — keep their catches
            // separate so a non-JSON response isn't logged as "network/timeout" (matches the
            // SimulateBundleAsync / ResolveInflationaryAmountsAsync convention).
            HttpResponseMessage response;
            try
            {
                response = await client.PostAsJsonAsync(_rpcUrl, rpc, ct);
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[{ReqId}] SimulationCanary: balance probe RPC failed (network/timeout)", item.ReqId);
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("[{ReqId}] SimulationCanary: balance probe eth_simulateV1 HTTP {StatusCode}",
                    item.ReqId, (int)response.StatusCode);
                return false;
            }

            JsonElement json;
            try
            {
                json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            }
            catch (JsonException jex)
            {
                _log.LogWarning(jex, "[{ReqId}] SimulationCanary: balance probe non-JSON response", item.ReqId);
                return false;
            }

            // Top-level JSON-RPC error (method unsupported, batch rejected) — surface it instead of
            // silently returning false, so a structurally broken probe is diagnosable.
            if (json.ValueKind == JsonValueKind.Object
                && json.TryGetProperty("error", out var rpcErr)
                && rpcErr.ValueKind != JsonValueKind.Null)
            {
                var errMsg = rpcErr.ValueKind == JsonValueKind.Object && rpcErr.TryGetProperty("message", out var m)
                    ? m.GetString() : rpcErr.ToString();
                _log.LogWarning("[{ReqId}] SimulationCanary: balance probe eth_simulateV1 rejected at RPC layer: {Error}",
                    item.ReqId, errMsg);
                return false;
            }

            if (!json.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Array
                || result.GetArrayLength() == 0
                || !result[0].TryGetProperty("calls", out var innerCalls)
                || innerCalls.ValueKind != JsonValueKind.Array)
            {
                _log.LogWarning("[{ReqId}] SimulationCanary: balance probe got an unusable eth_simulateV1 envelope", item.ReqId);
                return false;
            }

            var drifts = InterpretBalanceProbeResults(innerCalls, pairs, unwrapCalls.Count, out var truncated);
            if (truncated)
                _log.LogWarning(
                    "[{ReqId}] SimulationCanary: balance probe response truncated — some (holder,token) pairs were not checked",
                    item.ReqId);

            foreach (var d in drifts)
            {
                BalanceDriftTotal.WithLabels(d.Bucket).Inc();
                _log.LogError(
                    "[{ReqId}] SimulationCanary: CACHE BALANCE DRIFT holder={Holder} token={Token} " +
                    "graphBlock={Block} onChainBalance={OnChain} needed={Needed} ratioBucket={Bucket} detector=probe",
                    item.ReqId, d.Holder, d.Token,
                    item.GraphBlock, d.OnChain, d.Needed, d.Bucket);
            }

            return drifts.Count > 0;
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Distinct error surface so a future probe regression doesn't masquerade as RPC noise.
            _log.LogError(ex, "[{ReqId}] SimulationCanary: balance probe failed", item.ReqId);
            return false;
        }
    }

    /// <summary>
    /// Pure interpretation of the probe's <c>eth_simulateV1</c> per-call results, isolated from HTTP so
    /// the drift gate is unit-testable (mirrors <see cref="ParseSimulateV1Response"/>). For each pair,
    /// reads the <c>balanceOf</c> result at offset <c>unwrapCount + j</c> (the prefix calls precede the
    /// balance reads), compares the path's required outflow to the on-chain balance, and yields a drift
    /// record ONLY for ≥2× over-asks (<c>ge_2x|ge_10x|ge_100x</c>) or a true zero-balance send. A
    /// failed/empty balanceOf sub-call (<c>null</c>) is skipped, never treated as zero. Stops at the
    /// first index beyond the response and reports it via <paramref name="truncated"/> — it never
    /// fabricates a balance for a missing result.
    /// </summary>
    internal static IReadOnlyList<BalanceDriftRecord> InterpretBalanceProbeResults(
        JsonElement innerCalls,
        IReadOnlyList<(string Holder, string Token, BigInteger Required)> pairs,
        int unwrapCount,
        out bool truncated)
    {
        truncated = false;
        var drifts = new List<BalanceDriftRecord>();
        if (innerCalls.ValueKind != JsonValueKind.Array)
            return drifts;

        int total = innerCalls.GetArrayLength();
        for (int j = 0; j < pairs.Count; j++)
        {
            int idx = unwrapCount + j;
            if (idx >= total)
            {
                truncated = true;
                break; // don't fabricate a balance for a missing result
            }

            // ParseConvertCallReturnData is a generic single-ABI-uint256-word decoder shared with the
            // inflationary resolver; here it decodes the balanceOf return. null ⇒ sub-call failed.
            var onChain = ParseConvertCallReturnData(innerCalls[idx]);
            if (onChain is null)
                continue; // balanceOf sub-call failed — can't compare, skip rather than false-positive

            var p = pairs[j];
            var bucket = RevertClassifier.DriftBucket(p.Required, onChain.Value);

            // Suppress sub-2× overshoot: indistinguishable from benign demurrage rounding.
            // zero_balance (onChain==0, required>0) is a real phantom (nothing to send), keep it.
            if (bucket is not ("ge_2x" or "ge_10x" or "ge_100x" or "zero_balance"))
                continue;

            drifts.Add(new BalanceDriftRecord(p.Holder, p.Token, onChain.Value, p.Required, bucket));
        }

        return drifts;
    }

    /// <summary>
    /// Computes per-(holder, token) NET required balance from the resolved transfers: the holder's
    /// outflow of a token MINUS the inflow it receives of that same token within the same path.
    /// Only strictly-positive net amounts are returned.
    ///
    /// <para>Netting is the soundness fix for pass-through intermediaries (#74): a group-mint router
    /// receives a collateral token and forwards the SAME token+amount on to the group. Hub.sol
    /// executes the flow matrix edge-by-edge in mint-sorted order (collateral-in before forward-out),
    /// so the router is credited before it sends and never needs a standing balance — its real
    /// <c>balanceOf</c> is legitimately zero. Summing outflow alone would report the full forwarded
    /// amount as "needed" against a zero balance and raise a phantom <c>zero_balance</c> drift. By
    /// subtracting the matching inflow, a balanced pass-through nets to zero (not flagged) while a
    /// genuine over-source (outflow &gt; inflow, e.g. a cache-inflated balance) still surfaces its
    /// unfunded excess.</para>
    ///
    /// <para>Outflow skips edges that need no pre-existing balance: mints from the zero address, and
    /// self-issuance where the sender IS the token's avatar (<c>from == token</c>: personal-token
    /// issuance and group mint). Inflow counts every positive credit of the token to the holder.
    /// Keys are lowercased addresses.</para>
    /// </summary>
    internal static Dictionary<(string Holder, string Token), BigInteger> AggregateRequiredOutflow(
        IReadOnlyList<TransferPathStep> transfers)
    {
        const string zero = "0x0000000000000000000000000000000000000000";
        var outflow = new Dictionary<(string, string), BigInteger>();
        var inflow = new Dictionary<(string, string), BigInteger>();

        foreach (var t in transfers)
        {
            var token = t.TokenOwner.ToLowerInvariant();
            if (!BigInteger.TryParse(t.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                || value <= 0)
                continue;

            var from = t.From.ToLowerInvariant();
            // Outflow: the sender must source 'token', unless it mints (from zero) or self-issues.
            if (from != zero && from != token)
            {
                var k = (from, token);
                outflow[k] = outflow.TryGetValue(k, out var e) ? e + value : value;
            }

            // Inflow: the receiver is credited 'token' in-path, available before any later send.
            var to = t.To.ToLowerInvariant();
            if (to != zero)
            {
                var k = (to, token);
                inflow[k] = inflow.TryGetValue(k, out var e) ? e + value : value;
            }
        }

        var required = new Dictionary<(string, string), BigInteger>();
        foreach (var (key, outAmount) in outflow)
        {
            var net = outAmount - (inflow.TryGetValue(key, out var inAmount) ? inAmount : BigInteger.Zero);
            if (net > 0)
                required[key] = net;
        }

        return required;
    }

    /// <summary>
    /// Encodes <c>balanceOf(address account, uint256 id)</c> calldata for the Circles Hub (ERC1155),
    /// where the token id is <c>uint256(uint160(tokenAvatarAddress))</c>. Callers pre-filter malformed
    /// addresses via <see cref="IsHexAddress"/>; the all-zero fallback in <see cref="AddressWord"/> is
    /// defence-in-depth so encoding can never throw inside the probe.
    /// </summary>
    internal static string EncodeBalanceOfCalldata(string holder, string token)
        => Erc1155BalanceOfSelector + AddressWord(holder) + AddressWord(token);

    /// <summary>True if <paramref name="address"/> is a 20-byte hex address (optionally 0x-prefixed).</summary>
    internal static bool IsHexAddress(string address)
    {
        var hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address;
        if (hex.Length != 40)
            return false;
        foreach (var c in hex)
            if (!Uri.IsHexDigit(c))
                return false;
        return true;
    }

    // A 20-byte address right-aligned into a 32-byte ABI word. Falls back to all-zero on a malformed
    // address so encoding never throws inside the probe (pairs are pre-filtered by IsHexAddress).
    private static string AddressWord(string address)
    {
        var hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address;
        hex = hex.ToLowerInvariant();
        if (hex.Length != 40)
            return new string('0', 64);
        foreach (var c in hex)
            if (!Uri.IsHexDigit(c))
                return new string('0', 64);
        return hex.PadLeft(64, '0');
    }
}
