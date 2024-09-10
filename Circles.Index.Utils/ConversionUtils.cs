using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.Utils;

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

    public static decimal CrcToTc(DateTime timestamp, decimal amount)
    {
        long ts = ConvertToUnixTimestamp(timestamp);

        decimal payoutAtTimestamp = GetCrcPayoutAt(ts);
        decimal result = amount / payoutAtTimestamp * 24m;

        return result;
    }

    public static decimal TcToCrc(DateTime timestamp, decimal amount)
    {
        long ts = ConvertToUnixTimestamp(timestamp);

        decimal payoutAtTimestamp = GetCrcPayoutAt(ts);
        decimal result = amount / 24m * payoutAtTimestamp;

        return result;
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