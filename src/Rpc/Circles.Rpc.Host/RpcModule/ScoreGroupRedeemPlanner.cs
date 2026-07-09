using System.Globalization;
using System.Numerics;
using Circles.Common.Dto;

namespace Circles.Rpc.Host;

/// <summary>
/// Pure (DB-free) allocation logic for <c>circles_findScoreGroupRedeemPath</c>. Kept separate from the
/// RPC module so the MIN/greedy clamping can be unit-tested without a database.
///
/// A redeem converts a score group's gCRC back into its backing collateral. The holder's demurraged
/// gCRC balance (capped by the requested amount) is a single shared budget; each collateral the
/// treasury holds caps how much of that collateral can be returned. Per collateral:
/// <c>redeemable(c) = MIN(remaining budget, treasuryBalance(c))</c>. Collaterals are filled greedily,
/// largest treasury holding first (a deterministic placeholder until the redeem contract defines the
/// real collateral-selection order). Output mirrors <c>circlesV2_findPath</c> so SDKs consume it as-is.
/// </summary>
public static class ScoreGroupRedeemPlanner
{
    /// <summary>A collateral the group's treasury holds, with its demurraged balance (the per-leg cap).</summary>
    public readonly record struct CollateralCap(string Collateral, BigInteger Balance);

    /// <summary>Validated, normalized redeem request inputs.</summary>
    public readonly record struct RedeemRequest(string Group, string Holder, BigInteger? RequestedAmount);

    /// <summary>
    /// Validates and normalizes the raw RPC inputs (DB-free, so it is unit-tested directly).
    /// Lowercases and format-checks <paramref name="group"/>/<paramref name="holder"/> as 0x-prefixed
    /// 40-hex addresses; parses <paramref name="amount"/> as a non-negative uint256 decimal cap
    /// (null/empty ⇒ no cap, i.e. redeem up to the full balance). Throws <see cref="ArgumentException"/>
    /// on any malformed input rather than silently returning a zero result.
    /// </summary>
    public static RedeemRequest ParseRequest(string? group, string? holder, string? amount)
    {
        var groupAddress = NormalizeAddress(group, nameof(group));
        var holderAddress = NormalizeAddress(holder, nameof(holder));

        BigInteger? requestedAmount = null;
        if (!string.IsNullOrWhiteSpace(amount))
        {
            // NumberStyles.None rejects signs, whitespace, and non-decimal forms, so "-1"/"+1"/"1.5"/
            // "0x10" all throw rather than being silently coerced. "0" is valid (a no-op redeem).
            if (!BigInteger.TryParse(amount.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                throw new ArgumentException("amount must be a non-negative uint256 decimal string", nameof(amount));
            requestedAmount = parsed;
        }

        return new RedeemRequest(groupAddress, holderAddress, requestedAmount);
    }

    private static string NormalizeAddress(string? value, string paramName)
    {
        var addr = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(addr))
            throw new ArgumentException($"{paramName} is required", paramName);
        if (!IsHexAddress(addr))
            throw new ArgumentException($"{paramName} must be a 0x-prefixed 40-hex-char address", paramName);
        return addr;
    }

    private static bool IsHexAddress(string lower)
    {
        if (lower.Length != 42 || lower[0] != '0' || lower[1] != 'x')
            return false;
        for (var i = 2; i < 42; i++)
        {
            var c = lower[i];
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Allocates <paramref name="entitlement"/> across <paramref name="caps"/> and returns a
    /// findPath-shaped response whose <c>transfers</c> are one collateral leg
    /// (<paramref name="treasury"/> → <paramref name="holder"/>) per allocated collateral, and whose
    /// <c>maxFlow</c> is the total redeemable gCRC (== Σ collateral, since redemption is 1:1 in value;
    /// the holder burns that much gCRC to receive the collateral). No gCRC "burn leg" is emitted: its
    /// on-chain destination depends on the not-yet-deployed redeem contract, and <c>maxFlow</c> already
    /// states the amount. Returns an empty <c>maxFlow:"0"</c> response when nothing can be redeemed.
    /// </summary>
    public static MaxFlowResponse Plan(
        BigInteger entitlement,
        IReadOnlyList<CollateralCap> caps,
        string holder,
        string treasury)
    {
        var empty = new MaxFlowResponse("0", new List<TransferPathStep>());
        if (entitlement <= BigInteger.Zero || caps.Count == 0)
            return empty;

        // Greedy-by-largest, tie-broken by address for deterministic output.
        var ordered = caps
            .Where(c => c.Balance > BigInteger.Zero)
            .OrderByDescending(c => c.Balance)
            .ThenBy(c => c.Collateral, StringComparer.Ordinal)
            .ToList();

        var legs = new List<TransferPathStep>();
        var remaining = entitlement;
        var total = BigInteger.Zero;
        foreach (var cap in ordered)
        {
            if (remaining <= BigInteger.Zero)
                break;

            var alloc = BigInteger.Min(remaining, cap.Balance); // the MIN clamp
            if (alloc <= BigInteger.Zero)
                continue;

            // Collateral leg: the treasury returns this collateral to the holder on redeem.
            legs.Add(new TransferPathStep
            {
                From = treasury,
                To = holder,
                TokenOwner = cap.Collateral,
                Value = alloc.ToString(CultureInfo.InvariantCulture)
            });
            remaining -= alloc;
            total += alloc;
        }

        if (total <= BigInteger.Zero)
            return empty;

        return new MaxFlowResponse(total.ToString(CultureInfo.InvariantCulture), legs);
    }
}
