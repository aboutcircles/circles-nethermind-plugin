using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.Utils;

using System;

public static class DemurrageConverter
{
    const decimal Gamma = 0.99980133200859895743m; // Daily demurrage factor
    const decimal Beta = 1.0001987074682146291562714890133039617432343970799554367508m;

    const long DemurrageWindow = 86400; // 1 day in seconds
    const long InflationDayZero = 1602720000L; // 15th October 2020

    // Method to compute the power of Γ^i (Gamma^i)
    public static decimal GammaPower(long i)
    {
        return (decimal)Math.Pow((double)Gamma, i);
    }

    public static decimal BetaPower(long i)
    {
        return (decimal)Math.Pow((double)Beta, i);
    }

    public static long Day(long timestamp)
    {
        // calculate which day the timestamp is in, rounding down
        // note: max uint64 is 2^64 - 1, so we can safely cast the result
        return (timestamp - InflationDayZero) / DemurrageWindow;
    }

    /// <summary>
    /// Converts an inflationary balance to a demurraged balance.
    /// </summary>
    /// <param name="demurragedBalance">The demurraged balance to convert.</param>
    /// <returns></returns>
    public static decimal ConvertDemurragedToInflationary(decimal demurragedBalance)
    {
        decimal CalculateInflationaryBalance()
        {
            /* Solidity code:
             *      // calculate the inflationary balance by dividing the balance by GAMMA^days
                  // note: GAMMA < 1, so dividing by a power of it, returns a bigger number,
                  //       so the numerical imprecision is in the least significant bits.
                  int128 i = Math64x64.pow(BETA_64x64, _dayUpdated);
                  return Math64x64.mulu(i, _balance);
             */
            var timestamp = DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds();
            return demurragedBalance * BetaPower(Day(timestamp));
        }

        decimal inflationaryAmount = CalculateInflationaryBalance();
        return inflationaryAmount;
    }

    public static decimal ConvertInflationaryToDemurraged(decimal inflationaryBalance)
    {
        /** From solidity:
         *       // calculate the demurrage value by multiplying the value by GAMMA^days
        // note: GAMMA < 1, so multiplying by a power of it, returns a smaller number,
        //       so we lose the least significant bits, but our ground truth is the demurrage value,
        //       and the inflationary value is a numerical approximation (where the least significant digits
        //       are not reliable).
        int128 r = Math64x64.pow(GAMMA_64x64, uint256(_day));
        return Math64x64.mulu(r, _inflationaryValue);
         */
        decimal CalculateDemurragedBalance()
        {
            var timestamp = DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds();
            return inflationaryBalance * GammaPower(Day(timestamp));
        }

        decimal demurragedAmount = CalculateDemurragedBalance();
        return demurragedAmount;
    }
}

public abstract class ConversionUtils
{
    private static readonly long CirclesInceptionTimestamp =
        ConvertToUnixTimestamp(new DateTime(2020, 10, 15, 0, 0, 0, DateTimeKind.Utc));

    private const decimal OneDayInMilliSeconds = 86400m * 1000m;
    private const decimal OneCirclesYearInDays = 365.25m;
    private const decimal OneCirclesYearInMilliSeconds = OneCirclesYearInDays * 24m * 60m * 60m * 1000m;

    public static long ConvertToUnixTimestamp(DateTime dateTime)
    {
        DateTime unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return (long)(dateTime - unixEpoch).TotalMilliseconds;
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
    /// <returns></returns>
    public static decimal CirclesToStaticCircles(decimal circlesBalance)
    {
        return CirclesToCrc(circlesBalance) * 3;
    }

    /// <summary>
    /// Converts a static Circles balance (inflationary) to a regular v2 personal Circles balance (demurraged).
    /// </summary>
    /// <param name="staticCirclesBalance"></param>
    /// <returns></returns>
    public static decimal StaticCirclesToCircles(decimal staticCirclesBalance)
    {
        return CrcToCircles(staticCirclesBalance / 3);
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

    public static UInt256 AddressToUInt256(Address address)
    {
        return new(address.Bytes, true);
    }

    public static Address UInt256ToAddress(UInt256 uint256)
    {
        return new Address(uint256.ToBigEndian()[12..].ToHexString());
    }
}