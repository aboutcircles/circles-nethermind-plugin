using System.Numerics;

namespace Circles.Common;

// one unit in 64.64
/// <summary>
/// Minimal unsigned subset of ABDK 64.64 math –
/// only what Circles needs (MulU + Pow).
/// </summary>
public static class Fixed64
{
    /// <summary>2⁶⁴ (one unit in Q64.64).</summary>
    public static readonly BigInteger ONE = BigInteger.One << 64;

    /// <summary>
    /// (x · y) >> 64  – floor, exactly like Solidity’s ABDK <c>mulu</c>.
    /// </summary>
    public static BigInteger MulU(BigInteger x64, BigInteger y)
        => (x64 * y) >> 64;

    /// <summary>
    /// Bit-exact clone of <c>ABDKMath64x64.powu</c> (unsigned branch).
    /// </summary>
    public static BigInteger Pow(BigInteger x64, ulong n)
    {
        if (n == 0) return ONE; // x⁰ = 1

        // Promote to Q127.127 (shift left 63) for extra head-room.
        BigInteger base127 = x64 << 63;
        BigInteger result = BigInteger.One << 127; // 1.0 in Q127.127

        while (n > 0)
        {
            if ((n & 1UL) != 0)
                result = MulRound127(result, base127); // result *= base

            base127 = MulRound127(base127, base127); // base  *= base
            n >>= 1;
        }

        // Convert Q127.127 → Q64.64  (shift right 63, floor)
        return result >> 63;
    }

    /// <summary>
    /// (a · b + ½) >> 127  – multiplication in Q127.127 with
    /// half-up rounding, perfectly mirroring the EVM implementation.
    /// </summary>
    private static BigInteger MulRound127(BigInteger a127, BigInteger b127)
    {
        BigInteger prod = a127 * b127;
        prod += BigInteger.One << 126; // +½ for round-half-up
        return prod >> 127;
    }
}
