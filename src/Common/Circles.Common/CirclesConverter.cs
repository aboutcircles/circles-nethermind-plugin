using System.Numerics;
using Nethermind.Int256;

namespace Circles.Common;

/// <summary>
/// Conversions between the three Circles value representations.
/// </summary>
/// <remarks>
/// <para><b>VALUE REPRESENTATIONS:</b></para>
/// <list type="table">
///   <listheader>
///     <term>Name</term>
///     <description>Description</description>
///   </listheader>
///   <item>
///     <term>CRC (attoCrc)</term>
///     <description>V1 inflationary tokens. Grows ~7% annually on-chain. Used by V1 Hub contracts.</description>
///   </item>
///   <item>
///     <term>Demurraged Circles (attoCircles)</term>
///     <description>V2 time-adjusted tokens. Decays ~7% annually. Native format for V2 Hub ERC1155.</description>
///   </item>
///   <item>
///     <term>Static Circles (staticAttoCircles)</term>
///     <description>Time-frozen V2 tokens. No decay. Used by V2 inflationary ERC20 wrappers.</description>
///   </item>
/// </list>
/// 
/// <para><b>CONVERSION FORMULAS:</b></para>
/// <list type="bullet">
///   <item>CRC → Circles: Uses V1 inflation factor based on period (31,556,952 seconds)</item>
///   <item>Circles ↔ Static: Uses γ^day where γ ≈ 0.9998 (daily decay for 7% annual demurrage)</item>
/// </list>
/// 
/// <para><b>DAY-LEVEL GRANULARITY:</b></para>
/// <para>
/// Demurrage calculations use day indices (not seconds). The day index is computed as:
/// <c>(timestamp - INFLATION_DAY_ZERO) / 86400</c>
/// This means balances only change at midnight UTC, matching the on-chain Hub behavior.
/// </para>
/// </remarks>
public static class CirclesConverter
{
    // ───────────────────────────────── constants ─────────────────────────────────
    private static readonly BigInteger
        GAMMA_64 = BigInteger.Parse("18443079296116538654"); // (1-0.07)^(1/365.25) * 2^64

    private static readonly BigInteger BETA_64 = BigInteger.Parse("18450409579521241655"); // 1 / GAMMA
    private const long SECONDS_PER_DAY = 86_400;
    private const uint INFLATION_DAY_ZERO_UNIX = 1_602_720_000; // 2020-10-15 00:00 UTC  (V1 Hub epoch)
    private const uint V2_INFLATION_DAY_ZERO_UNIX = 1_675_209_600; // 2023-02-01 00:00 UTC  (V2 Hub epoch)
    private const decimal ATTO_FACTOR_DEC = 1_000_000_000_000_000_000m; // 1e18

    private static readonly UInt256 FactorA = UInt256.Parse("1000000000000"); // 10^12
    private static readonly BigInteger FactorB = BigInteger.Parse("1000000000000"); // 10^12

    // ───────────────────────────── public high-level API ──────────────────────────

    /// <summary>
    /// Converts an atto-Circles amount (18 decimals, BigInteger) to whole
    /// Circles as a <see cref="decimal"/>.  
    /// Throws <see cref="OverflowException"/> if the value cannot fit inside
    /// <c>decimal</c> (≈ 7.9 × 10²⁸).
    /// </summary>
    public static decimal AttoCirclesToCircles(BigInteger attoCircles)
    {
        if (attoCircles.IsZero) return 0m;

        // Ensure magnitude fits inside decimal before casting
        var abs = BigInteger.Abs(attoCircles);
        if (abs > (BigInteger)decimal.MaxValue)
            throw new OverflowException("Atto-value too large for decimal.");

        return (decimal)attoCircles / ATTO_FACTOR_DEC; // exact-scale division
    }

    /// <summary>
    /// Converts a UI-friendly Circles amount (<c>decimal</c>) into the 18-decimal
    /// atto-representation used by the contracts.  
    /// The conversion is **truncated** (floored) to match Solidity’s behaviour.
    /// </summary>
    public static BigInteger CirclesToAttoCircles(decimal circles)
    {
        // Multiply first, then truncate to avoid rounding up
        decimal raw = decimal.Truncate(circles * ATTO_FACTOR_DEC);

        // The explicit cast is defined in .NET 6+ and is exact for integral decimals
        return (BigInteger)raw;
    }

    /// <summary>
    /// CRC&nbsp;→ demurraged Circles, using only a block-timestamp.
    /// </summary>
    public static BigInteger AttoCrcToAttoCircles(
        BigInteger v1Amount,
        ulong blockTimestampUtc)
    {
        const uint PERIOD_SEC = 31_556_952; // constructor arg [1]
        ulong secondsSinceEpoch = blockTimestampUtc - INFLATION_DAY_ZERO_UNIX;

        uint periodIdx = (uint)(secondsSinceEpoch / PERIOD_SEC);
        uint secondsInto = (uint)(secondsSinceEpoch % PERIOD_SEC);

        BigInteger factorCur = V1Inflation.Factor(periodIdx);
        BigInteger factorNext = V1Inflation.Factor(periodIdx + 1);

        return V1Converter.V1ToDemurrage(
            v1Amount, factorCur, factorNext, secondsInto, PERIOD_SEC);
    }

    /// <summary>
    /// Demurraged Circles&nbsp;→ CRC, inverse of the above.
    /// </summary>
    public static BigInteger AttoCirclesToAttoCrc(
        BigInteger v2Demurraged,
        ulong blockTimestampUtc)
    {
        const uint PERIOD_SEC = 31_556_952;
        ulong secondsSinceEpoch = blockTimestampUtc - INFLATION_DAY_ZERO_UNIX;

        uint periodIdx = (uint)(secondsSinceEpoch / PERIOD_SEC);
        uint secondsInto = (uint)(secondsSinceEpoch % PERIOD_SEC);

        BigInteger factorCur = V1Inflation.Factor(periodIdx);
        BigInteger factorNext = V1Inflation.Factor(periodIdx + 1);

        // exact inverse of V1ToDemurrage
        BigInteger rP = factorCur * (PERIOD_SEC - secondsInto)
                        + factorNext * secondsInto;

        return (v2Demurraged * rP) / (3 * V1Converter.ACCURACY * PERIOD_SEC);
    }

    /// <summary>V2 demurraged Circles → V2 Static (inflationary) Circles, for “today”.</summary>
    public static BigInteger AttoCirclesToAttoStaticCircles(BigInteger attoCircles) =>
        DemurrageToInflationary(attoCircles, Today());

    /// <summary>V2 Static (inflationary) Circles → V2 demurraged Circles, for “today”.</summary>
    public static BigInteger AttoStaticCirclesToAttoCircles(BigInteger attoStaticCircles) =>
        InflationaryToDemurrage(attoStaticCircles, Today());

    private static ulong Today() =>
        DayFromTimestamp(DateTimeOffset.UtcNow, V2_INFLATION_DAY_ZERO_UNIX);

    /// <summary>Unix timestamp → Circles day index.</summary>
    public static ulong DayFromTimestamp(DateTimeOffset ts, uint inflationDayZeroUnix)
    {
        ulong seconds = (ulong)(ts.ToUnixTimeSeconds() - inflationDayZeroUnix);
        return seconds / SECONDS_PER_DAY;
    }

    /// <summary>Inflationary → demurraged for an explicit day index.</summary>
    public static BigInteger InflationaryToDemurrage(BigInteger inflationary, ulong day)
    {
        var factor = Fixed64.Pow(GAMMA_64, day);
        return Fixed64.MulU(factor, inflationary); // same floor-rounding as Solidity
    }

    /// <summary>Demurraged → inflationary for an explicit day index.</summary>
    public static BigInteger DemurrageToInflationary(BigInteger demurraged, ulong day)
    {
        var factor = Fixed64.Pow(BETA_64, day);
        return Fixed64.MulU(factor, demurraged); // same floor-rounding as Solidity
    }

    // Convert Wei (1e18 base) to an integer with 6 decimals of precision (divide by 10^12).
    public static long TruncateToInt64(BigInteger wei)
    {
        // Discard 12 decimals
        BigInteger truncated = wei / FactorB;

        // Clamp if above long.MaxValue
        if (truncated > (BigInteger)long.MaxValue)
            truncated = long.MaxValue;

        return (long)truncated;
    }

    // Convert the 6-decimal integer back to Wei by multiplying by 10^12.
    public static BigInteger BlowUpToBigInteger(long sixDecimalValue)
    {
        return sixDecimalValue * FactorB;
    }

    // Convert Wei (1e18 base) to an integer with 6 decimals of precision (divide by 10^12).
    public static long TruncateToInt64(UInt256 wei)
    {
        // Discard 12 decimals
        UInt256 truncated = wei / FactorA;

        // Clamp if above long.MaxValue
        if (truncated > (UInt256)long.MaxValue)
            truncated = (UInt256)long.MaxValue;

        return (long)truncated;
    }

    // Convert the 6-decimal integer back to Wei by multiplying by 10^12.
    public static UInt256 BlowUpToUInt256(long sixDecimalValue)
    {
        return (UInt256)sixDecimalValue * FactorA;
    }
}