using System.Globalization;
using System.Numerics;
using Circles.Common;
using Circles.Common.Dto;

namespace Circles.Pathfinder.Host.Canary;

/// <summary>
/// Unwrap-prefix calldata builders for the canary's eth_simulateV1 bundle path.
/// Carries the two-stage type discriminant (Demurraged → Resolved) that makes the unit
/// conversion step compile-time mandatory for InflationaryCircles wrappers.
/// </summary>
internal sealed partial class SimulationCanaryService
{
    /// <summary>
    /// `function unwrap(uint256)` ABI selector — keccak256("unwrap(uint256)")[:4].
    /// </summary>
    private const string UnwrapSelector = "0xde0e9a3e";

    /// <summary>
    /// Output of <see cref="BuildUnwrapPrefix"/>: an unwrap call whose <see cref="DemurragedAmount"/>
    /// is in 1155 ledger units, NOT the wrapper's native unwrap-argument unit. Must pass through
    /// <see cref="ResolveInflationaryAmountsAsync"/> before it can be encoded as calldata.
    /// <para>This is the "raw" form. <see cref="WrapperType"/> carries the discriminant the
    /// resolver needs:</para>
    /// <list type="bullet">
    ///   <item><see cref="CirclesType.DemurrageCircles"/>: unwrap(_amount) takes demurraged
    ///     units 1:1 — resolver passes <c>DemurragedAmount</c> through unchanged.</item>
    ///   <item><see cref="CirclesType.InflationaryCircles"/>: unwrap(_amount) takes inflation-
    ///     corrected ERC20 units — resolver calls <c>convertDemurrageToInflationaryValue</c>
    ///     to convert before bundle assembly.</item>
    /// </list>
    /// Verified on-chain by direct eth_simulateV1 probes against both wrapper types (2026-05-27).
    /// <para>Unlike <see cref="ResolvedUnwrapCall"/>, this type uses the positional record-struct
    /// constructor (effectively public to the canary assembly). The asymmetry is intentional:
    /// Demurraged is the raw projection of <see cref="BuildUnwrapPrefix"/> inputs (1155 ledger
    /// amounts plus a wrapper-type discriminant) with no derived invariant beyond what the
    /// inputs already guarantee, so there is no factory-only invariant to encapsulate. Resolved
    /// encodes the unit-conversion step (γ^day for inflationary wrappers) and must be reachable
    /// only via the resolver pipeline — hence its private constructor.</para>
    /// </summary>
    internal readonly record struct DemurragedUnwrapCall(
        string From, string Wrapper, BigInteger DemurragedAmount, CirclesType WrapperType);

    /// <summary>
    /// Output of <see cref="ResolveInflationaryAmountsAsync"/>: an unwrap call whose
    /// <see cref="Amount"/> is in the wrapper's NATIVE unit (whatever <c>unwrap(uint256)</c>
    /// expects on-chain). Direct input to <see cref="SimulateBundleAsync"/>.
    /// <para>Splitting this from <see cref="DemurragedUnwrapCall"/> makes the unit conversion
    /// step compile-time mandatory: passing a <see cref="DemurragedUnwrapCall"/> to
    /// <see cref="SimulateBundleAsync"/> is a type error — the exact regression class of PR #408.</para>
    /// <para>The constructor is private: instances can only be produced via
    /// <see cref="FromDemurraged"/> (1:1 lift for <see cref="CirclesType.DemurrageCircles"/> or as
    /// resolver-failure fallback) or <see cref="FromInflated"/> (substitute the γ^day-converted
    /// amount for an <see cref="CirclesType.InflationaryCircles"/> wrapper). Both factories take
    /// a <see cref="DemurragedUnwrapCall"/> as input — the resolver pipeline is the only path
    /// that can yield a Resolved call, so a future contributor cannot accidentally bypass unit
    /// discrimination by synthesizing one in the bundle assembler.</para>
    /// </summary>
    internal readonly record struct ResolvedUnwrapCall
    {
        public string From { get; }
        public string Wrapper { get; }
        public BigInteger Amount { get; }

        private ResolvedUnwrapCall(string from, string wrapper, BigInteger amount)
        {
            From = from;
            Wrapper = wrapper;
            Amount = amount;
        }

        /// <summary>
        /// Lift a <see cref="DemurragedUnwrapCall"/> 1:1. Used for
        /// <see cref="CirclesType.DemurrageCircles"/> wrappers (unwrap argument == demurraged
        /// amount) and as the fallback when the inflationary resolver RPC fails for one wrapper
        /// — the canary still attempts the bundle, which produces the prior false-positive class
        /// for that one inflationary entry rather than silently skipping coverage.
        /// </summary>
        internal static ResolvedUnwrapCall FromDemurraged(DemurragedUnwrapCall call)
            => new(call.From, call.Wrapper, call.DemurragedAmount);

        /// <summary>
        /// Lift a <see cref="DemurragedUnwrapCall"/> through the inflationary conversion (γ^day)
        /// for a <see cref="CirclesType.InflationaryCircles"/> wrapper. The caller is responsible
        /// for sourcing <paramref name="inflatedAmount"/> from
        /// <c>convertDemurrageToInflationaryValue</c> at the simulation block — the type system
        /// does not enforce that the amount actually matches the wrapper's day-index conversion.
        /// <para>Throws <see cref="ArgumentOutOfRangeException"/> if <paramref name="inflatedAmount"/>
        /// is negative. <c>ParseConvertCallReturnData</c> prepends "0" to force unsigned parsing
        /// of the resolver RPC return value, so a negative amount cannot reach this factory via
        /// the normal pipeline; the guard is defense-in-depth against a future caller that
        /// bypasses the parser.</para>
        /// </summary>
        internal static ResolvedUnwrapCall FromInflated(DemurragedUnwrapCall call, BigInteger inflatedAmount)
        {
            if (inflatedAmount < 0)
                throw new ArgumentOutOfRangeException(nameof(inflatedAmount), inflatedAmount,
                    "Inflationary unwrap amount must be non-negative.");
            return new ResolvedUnwrapCall(call.From, call.Wrapper, inflatedAmount);
        }
    }

    /// <summary>
    /// For each transfer whose TokenOwner is a known wrapper, attribute one unwrap call
    /// to the transfer's From address for the full transfer value. Groups identical
    /// (from, wrapper) pairs and sums amounts so a single SDK-equivalent unwrap precedes
    /// the operateFlowMatrix call in the bundle.
    /// <para>The <paramref name="inflationaryWrappers"/> set discriminates which wrappers
    /// need <c>convertDemurrageToInflationaryValue</c> at resolve-time. Wrappers absent
    /// from the set are treated as <see cref="CirclesType.DemurrageCircles"/> (unit pass-through).</para>
    /// </summary>
    internal static IReadOnlyList<DemurragedUnwrapCall> BuildUnwrapPrefix(
        List<TransferPathStep> transfers,
        IReadOnlyDictionary<string, string>? wrapperToAvatar,
        IReadOnlySet<string>? inflationaryWrappers = null)
    {
        if (wrapperToAvatar == null || wrapperToAvatar.Count == 0)
            return Array.Empty<DemurragedUnwrapCall>();

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
            return Array.Empty<DemurragedUnwrapCall>();

        var calls = new List<DemurragedUnwrapCall>(order.Count);
        foreach (var key in order)
        {
            var wrapperType = inflationaryWrappers != null && inflationaryWrappers.Contains(key.Wrapper)
                ? CirclesType.InflationaryCircles
                : CirclesType.DemurrageCircles;
            calls.Add(new DemurragedUnwrapCall(key.From, key.Wrapper, sums[key], wrapperType));
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
    /// Promotes every <see cref="DemurragedUnwrapCall"/> to a <see cref="ResolvedUnwrapCall"/>
    /// 1:1 using its <c>DemurragedAmount</c>. Used in the all-DemurrageCircles fast path
    /// (no conversion needed) and as the fallback for resolver failures (preserves pre-PR-#408
    /// false-positive class for inflationary wrappers — never silently drops the canary).
    /// </summary>
    private static IReadOnlyList<ResolvedUnwrapCall> PromoteAllDemurraged(IReadOnlyList<DemurragedUnwrapCall> calls)
    {
        var resolved = new List<ResolvedUnwrapCall>(calls.Count);
        for (int i = 0; i < calls.Count; i++)
            resolved.Add(ResolvedUnwrapCall.FromDemurraged(calls[i]));
        return resolved;
    }
}
