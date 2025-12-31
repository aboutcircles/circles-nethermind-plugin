using System.Numerics;
using Nethermind.Int256;

namespace Circles.Common;
// adjust to whatever namespace you use

public static class BigIntegerExtensions
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private const char MultibasePrefix = 'z';

    /// <summary>
    /// Converts a non-negative <see cref="BigInteger"/> to a Base58-BTC string.
    /// </summary>
    /// <param name="value">Number to encode (must be ≥ 0).</param>
    /// <param name="withPrefix">If <c>true</c>, prepends the multibase
    ///                           prefix 'z' (Base58-BTC as per multibase spec).</param>
    /// <returns>Base58-BTC encoded string.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="value"/> is negative.</exception>
    public static string ToBase58Btc(this BigInteger value, bool withPrefix = false)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Value must be non-negative.");

        // Special-case zero (alphabet[0] == '1')
        if (value.IsZero)
            return withPrefix ? $"{MultibasePrefix}{Alphabet[0]}" : Alphabet[0].ToString();

        Span<char> buffer = stackalloc char[64]; // enough for 2^512-1
        int pos = buffer.Length;

        BigInteger current = value;
        while (current > 0)
        {
            current = BigInteger.DivRem(current, 58, out var remainder);
            buffer[--pos] = Alphabet[(int)remainder];
        }

        if (withPrefix)
            buffer[--pos] = MultibasePrefix;

        return new string(buffer[pos..]);
    }

    /// <summary>
    /// Converts a non-negative <see cref="BigInteger"/> to a Base58-BTC string.
    /// </summary>
    /// <param name="value">Number to encode (must be ≥ 0).</param>
    /// <param name="withPrefix">If <c>true</c>, prepends the multibase
    ///                           prefix 'z' (Base58-BTC as per multibase spec).</param>
    /// <returns>Base58-BTC encoded string.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="value"/> is negative.</exception>
    /// <summary>
    /// Base58-BTC encoder for <see cref="UInt256"/>.
    /// </summary>
    /// <inheritdoc cref="ToBase58Btc(System.Numerics.BigInteger,bool)"/>
    public static string ToBase58Btc(this UInt256 value, bool withPrefix = false)
        => ((BigInteger)value).ToBase58Btc(withPrefix);
}