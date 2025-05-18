using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.Utils;

using System;

public abstract class ConversionUtils
{
    private static readonly DateTime CirclesInceptionDate = new(2020, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    private static readonly long CirclesInceptionTimestamp =
        ConvertToUnixTimestamp(CirclesInceptionDate);

    private const decimal OneDayInMilliSeconds = 86400m * 1000m;
    private const decimal OneCirclesYearInDays = 365.25m;
    private const decimal OneCirclesYearInMilliSeconds = OneCirclesYearInDays * 24m * 60m * 60m * 1000m;
    private const decimal Beta = 1.0001987074682146291562714890133039617432343970799554367508m;

    private static readonly UInt256 Factor = UInt256.Parse("1000000000000"); // 10^12

    // Convert Wei (1e18 base) to an integer with 6 decimals of precision (divide by 10^12).
    public static long TruncateToInt64(UInt256 wei)
    {
        // Discard 12 decimals
        UInt256 truncated = wei / Factor;

        // Clamp if above long.MaxValue
        if (truncated > (UInt256)long.MaxValue)
            truncated = (UInt256)long.MaxValue;

        return (long)truncated;
    }

    // Convert the 6-decimal integer back to Wei by multiplying by 10^12.
    public static UInt256 BlowUpToUInt256(long sixDecimalValue)
    {
        return (UInt256)sixDecimalValue * Factor;
    }

    public static long ConvertToUnixTimestamp(DateTime dateTime)
    {
        DateTimeOffset dateTimeOffset = dateTime;
        return dateTimeOffset.ToUnixTimeMilliseconds();
    }

    public static decimal GetCrcPayoutAt(long timestamp)
    {
        decimal daysSinceCirclesInception = (timestamp - CirclesInceptionTimestamp) / OneDayInMilliSeconds;
        decimal circlesYearsSince = (timestamp - CirclesInceptionTimestamp) / OneCirclesYearInMilliSeconds;
        decimal daysInCurrentCirclesYear = daysSinceCirclesInception % OneCirclesYearInDays;
        decimal initialDailyCrcPayout = 8;

        decimal circlesPayoutInCurrentYear = initialDailyCrcPayout;
        decimal previousCirclesPerDayValue = initialDailyCrcPayout;

        for (int index = 0; index < circlesYearsSince; index++)
        {
            previousCirclesPerDayValue = circlesPayoutInCurrentYear;
            circlesPayoutInCurrentYear *= 1.07m;
        }

        decimal x = previousCirclesPerDayValue;
        decimal y = circlesPayoutInCurrentYear;
        decimal a = daysInCurrentCirclesYear / OneCirclesYearInDays;

        decimal result = x * (1 - a) + y * a;

        return result;
    }

    /// <summary>
    /// Converts a v1 inflationary CRC balance to a v2 personal Circles balance (demurraged).
    /// </summary>
    /// <param name="crcBalance"></param>
    /// <returns></returns>
    public static decimal CrcToCircles(decimal crcBalance)
    {
        long ts = ConvertToUnixTimestamp(DateTime.Now);

        decimal payoutAtTimestamp = GetCrcPayoutAt(ts);
        decimal result = crcBalance / payoutAtTimestamp * 24m;

        return result;
    }

    /// <summary>
    /// Converts a regular v2 personal Circles balance (demurraged) to a v1 compatible CRC balance (inflationary).
    /// </summary>
    /// <param name="circlesBalance"></param>
    /// <returns></returns>
    public static decimal CirclesToCrc(decimal circlesBalance)
    {
        long ts = ConvertToUnixTimestamp(DateTime.Now);

        decimal payoutAtTimestamp = GetCrcPayoutAt(ts);
        decimal result = circlesBalance / 24m * payoutAtTimestamp;

        return result;
    }

    /// <summary>
    /// Converts a regular v2 personal Circles balance (demurraged) to a static Circles balance (inflationary).
    /// </summary>
    /// <param name="circlesBalance"></param>
    /// <param name="lastUpdate">The day the demurrage value was last updated since inflation_day_zero</param>
    /// <returns></returns>
    public static decimal CirclesToStaticCircles(decimal circlesBalance, DateTime lastUpdate)
    {
        var lastUpdateDay = Math.Floor((lastUpdate - CirclesInceptionDate).TotalDays);
        var f = (decimal)Math.Pow((double)Beta, lastUpdateDay);
        return f * circlesBalance;
    }

    /// <summary>
    /// Converts a static Circles balance (inflationary) to a regular v2 personal Circles balance (demurraged).
    /// </summary>
    /// <param name="staticCirclesBalance"></param>
    /// <param name="lastUpdate"></param>
    /// <returns></returns>
    public static decimal StaticCirclesToCircles(decimal staticCirclesBalance, DateTime? lastUpdate = null)
    {
        var lastUpdate_ = (lastUpdate ?? DateTime.Now).ToUniversalTime();
        var lastUpdateDay = Math.Floor((lastUpdate_ - CirclesInceptionDate).TotalDays);
        var f = (decimal)Math.Pow((double)Beta, lastUpdateDay);
        return staticCirclesBalance / f;
    }

    public static decimal AttoCirclesToCircles(UInt256 weiBalance)
    {
        // Convert to Ether by dividing by 10^18 (Ether has 18 decimal places)
        BigInteger weiInEth = BigInteger.Pow(new BigInteger(10), 18);
        decimal etherValue = (decimal)weiBalance / (decimal)weiInEth;

        // Returning the converted value as a decimal
        return etherValue;
    }

    public static UInt256 CirclesToAttoCircles(decimal circlesBalance)
    {
        // Convert to Wei by multiplying by 10^18 (Ether has 18 decimal places)
        BigInteger weiInEth = BigInteger.Pow(new BigInteger(10), 18);
        decimal weiValue = circlesBalance * (decimal)weiInEth;

        // Returning the converted value as a UInt256
        var bigint = new BigInteger(weiValue);
        return (UInt256)bigint;
    }

    public static long TimestampToCirclesDay(DateTime timestamp)
    {
        var day = (timestamp - CirclesInceptionDate).TotalDays;
        return (long)Math.Floor(day);
    }

    public static long TimestampToCirclesDay(long timestamp)
    {
        var day = DateTimeOffset.FromUnixTimeSeconds(timestamp - CirclesInceptionTimestamp).Offset.TotalDays;
        return (long)Math.Floor(day);
    }

    public static UInt256 AddressToUInt256(Address address)
    {
        return new(address.Bytes, true);
    }

    public static Address UInt256ToAddress(UInt256 uint256)
    {
        return new Address(uint256.ToBigEndian()[12..].ToHexString());
    }
}