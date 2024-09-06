using System.Globalization;
using System.Numerics;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Int256;

namespace Circles.Index.Utils;

public abstract class ConversionUtils
{
    // public static decimal ToTimeCircles(UInt256 tokenBalance)
    // {
    //     var balance = FormatCircles(tokenBalance);
    //     var tcBalance = TimeCirclesConverter.CrcToTc(DateTime.Now, decimal.Parse((string)balance));
    //
    //     return tcBalance;
    // }
    //
    // public static decimal ToInflationCircles(UInt256 tokenBalance)
    // {
    //     var balance = FormatCircles(tokenBalance);
    //     var crcBalance = TimeCirclesConverter.TcToCrc(DateTime.Now, decimal.Parse((string)balance));
    //
    //     return crcBalance;
    // }
    //
    // public static string FormatCircles(UInt256 tokenBalance)
    // {
    //     var ether = BigInteger.Divide((BigInteger)tokenBalance, BigInteger.Pow(10, 18));
    //     var remainder = BigInteger.Remainder((BigInteger)tokenBalance, BigInteger.Pow(10, 18));
    //     var remainderString = remainder.ToString("D18").TrimEnd('0');
    //
    //     return remainderString.Length > 0
    //         ? $"{ether}.{remainderString}"
    //         : ether.ToString(CultureInfo.InvariantCulture);
    // }

    public static UInt256 AddressToUInt256(Address address)
    {
        return new(address.Bytes, true);
    }
}