using System.Numerics;

namespace Circles.Common;

public class CidHelper
{
    private static readonly char[] _b58Alphabet =
        "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz".ToCharArray();

    public static string ToBase58(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
            return string.Empty;

        // Count leading zeros.
        int zeros = 0;
        int idx = 0;
        while (idx < input.Length && input[idx] == 0)
        {
            zeros++;
            idx++;
        }

        // Convert to BigInteger for simplicity (input is big-endian).
        var value = new BigInteger(input.ToArray().Reverse().Append((byte)0).ToArray()); // positive
        Span<char> tmp = stackalloc char[input.Length * 2]; // upper bound
        int tmpPos = tmp.Length;

        while (value > 0)
        {
            value = BigInteger.DivRem(value, 58, out var rem);
            tmp[--tmpPos] = _b58Alphabet[(int)rem];
        }

        while (zeros-- > 0)
            tmp[--tmpPos] = _b58Alphabet[0];

        return new string(tmp.Slice(tmpPos));
    }

    public static string MetadataDigestToCidV0(byte[] metadataDigest)
    {
        byte[] multihash = new byte[34];
        multihash[0] = 0x12; // multicodec: sha2-256
        multihash[1] = 0x20; // digest length: 32 bytes
        Buffer.BlockCopy(metadataDigest, 0, multihash, 2, 32);

        var cidV0 = CidHelper.ToBase58(multihash);
        return cidV0;
    }
}