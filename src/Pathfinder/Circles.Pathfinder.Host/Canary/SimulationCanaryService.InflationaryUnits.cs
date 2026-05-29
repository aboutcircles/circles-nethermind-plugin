using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Circles.Common;
using Circles.Pathfinder.Data;

namespace Circles.Pathfinder.Host.Canary;

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

/// <summary>
/// Calldata encoders and parsers for the inflationary unit conversion step.
/// </summary>
internal sealed partial class SimulationCanaryService
{
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
    /// Promotes a list of <see cref="DemurragedUnwrapCall"/> to <see cref="ResolvedUnwrapCall"/>,
    /// substituting the resolver's inflationary amounts only into <see cref="CirclesType.InflationaryCircles"/>
    /// positions. Demurraged entries pass through (their <c>DemurragedAmount</c> already IS the
    /// native unwrap-argument unit). A null resolved amount (RPC/parse failure) falls back to
    /// the demurraged amount — the canary still attempts a simulation, which just produces the
    /// prior false-positive class for that one inflationary wrapper, not silently skipping coverage.
    /// </summary>
    internal static IReadOnlyList<ResolvedUnwrapCall> ApplyInflationaryAmounts(
        IReadOnlyList<DemurragedUnwrapCall> calls,
        IReadOnlyList<BigInteger?> inflationaryAmountsForInflationaryCalls,
        ILogger? log = null,
        string? reqIdForLog = null)
    {
        var resolved = new List<ResolvedUnwrapCall>(calls.Count);
        int infIdx = 0;
        for (int i = 0; i < calls.Count; i++)
        {
            var src = calls[i];
            if (src.WrapperType != CirclesType.InflationaryCircles)
            {
                resolved.Add(ResolvedUnwrapCall.FromDemurraged(src));
                continue;
            }
            BigInteger? resolvedAmount = infIdx < inflationaryAmountsForInflationaryCalls.Count
                ? inflationaryAmountsForInflationaryCalls[infIdx]
                : null;
            infIdx++;
            if (!resolvedAmount.HasValue)
            {
                resolved.Add(ResolvedUnwrapCall.FromDemurraged(src));
                continue;
            }
            var bumped = ApplyInflationaryRoundtripBump(resolvedAmount.Value);
            var delta = bumped - resolvedAmount.Value;
            if (delta > 0)
            {
                InflationaryBumpApplied.Inc();
                if (log != null && log.IsEnabled(LogLevel.Debug))
                {
                    log.LogDebug(
                        "[{ReqId}] SimulationCanary: inflationary roundtrip bump applied wrapper={Wrapper} from={From} rawInflated={RawInflated} bumped={Bumped} delta={Delta}",
                        reqIdForLog ?? "-", src.Wrapper, src.From, resolvedAmount.Value, bumped, delta);
                }
            }
            resolved.Add(ResolvedUnwrapCall.FromInflated(src, bumped));
        }
        return resolved;
    }

    /// <summary>
    /// On-chain `convertDemurrageToInflationaryValue(D, day)` followed by
    /// `convertInflationaryToDemurrageValue(I, day)` (which Hub.sol's `unwrap()`
    /// invokes internally) is NOT a left-inverse: each `Math64x64.mulu` floors,
    /// so the demurraged amount the holder actually receives lands in `[D - ε, D]`.
    /// Empirically (probed at day 2051) the gap scales linearly with D at ratio
    /// ≈ 3.43e-14, e.g. 342,753 wei short on 10,000 CRC. The canary then asks
    /// `operateFlowMatrix` to forward exactly D and reverts with
    /// `ERC1155InsufficientBalance` at `stage=flow_matrix` — a canary-only
    /// false positive (real SDK broadcasts unwrap their full ERC20 balance, not
    /// "minimum I"). Adding `I / 1e12` (one part per trillion) clears the gap
    /// with ~30,000× safety vs the observed ratio. Worst-case future drift is
    /// bounded by `2 · day · 2^-64` (Math64x64 has fixed 64-bit fractional
    /// precision; two stacked floors over a β^day chain), ≈ 1.1e-16 at day=2051
    /// and well under 1e-12 for any plausible future day index. The bump stays
    /// six orders of magnitude below the demurrage safety headroom
    /// (<c>1 - Settings.DemurrageSafetyMargin</c> ≈ 1e-6 at the default 0.999999)
    /// so it does not eat into the balance-side margin that protects max-flow
    /// paths. For tiny I (&lt; 1e12 wei, well below any realistic canary value)
    /// integer division floors to zero — no bump applied.
    /// </summary>
    internal static BigInteger ApplyInflationaryRoundtripBump(BigInteger rawInflatedAmount)
    {
        // Negatives are blocked at the type boundary by `ResolvedUnwrapCall.FromInflated`;
        // zero passes through unchanged so an upstream change that legitimately yields
        // I=0 (no inflationary unwrap needed) doesn't acquire a spurious +1 wei.
        if (rawInflatedAmount == 0) return rawInflatedAmount;
        return rawInflatedAmount + (rawInflatedAmount / 1_000_000_000_000);
    }
}
