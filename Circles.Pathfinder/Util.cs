using Nethermind.Int256;

namespace Circles.Pathfinder;

public static class WeiConverter
{
    private static readonly UInt256 Factor = UInt256.Parse("1000000000000"); // 10^12

    // Convert Wei (1e18 base) to an integer with 6 decimals of precision (divide by 10^12).
    public static long TruncateToInt64(this UInt256 wei)
    {
        // Discard 12 decimals
        UInt256 truncated = wei / Factor;

        // Clamp if above long.MaxValue
        if (truncated > (UInt256)long.MaxValue)
            truncated = (UInt256)long.MaxValue;

        return (long)truncated;
    }

    // Convert the 6-decimal integer back to Wei by multiplying by 10^12.
    public static UInt256 BlowUpToUInt256(this long sixDecimalValue)
    {
        return (UInt256)sixDecimalValue * Factor;
    }
}