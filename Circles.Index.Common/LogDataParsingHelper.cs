using System.Numerics;
using Nethermind.Int256;

namespace Circles.Index.Common;

/// <summary>
/// Helper methods for decoding standard Solidity event log data
/// with dynamic arrays / bytes.
/// </summary>
public static class LogDataParsingHelper
{
    /// <summary>
    /// Reads a 32-byte offset from the specified location.
    /// The returned offset is relative to the start of `log.Data`.
    /// Returns -1 if we can't read a full 32 bytes there.
    /// </summary>
    public static int ParseOffset(ReadOnlySpan<byte> data, int offsetOfOffset)
    {
        if (offsetOfOffset + 32 > data.Length)
        {
            // Not enough bytes to parse
            return -1;
        }

        // The offset is stored as a 256-bit integer (Big Endian).
        return (int)new BigInteger(data.Slice(offsetOfOffset, 32).ToArray(), true, true);
    }

    /// <summary>
    /// Reads a dynamic array of UInt256 from the log data at the given offset:
    ///  - The first 32 bytes at that offset is the array length,
    ///  - Then arrayLength * 32 bytes of actual data.
    /// Returns an empty array if there's not enough data to decode properly.
    /// </summary>
    public static UInt256[] ParseUInt256Array(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 32 > data.Length)
        {
            // Offset out of range or not enough bytes for length
            return [];
        }

        int length = (int)new BigInteger(data.Slice(offset, 32).ToArray(), true, true);
        int dataStart = offset + 32;
        int totalNeeded = dataStart + length * 32;

        if (length < 0 || totalNeeded > data.Length)
        {
            // Negative length or out of range
            return [];
        }

        var result = new UInt256[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = new UInt256(data.Slice(dataStart + i * 32, 32), true);
        }

        return result;
    }

    /// <summary>
    /// Reads a dynamic bytes object from the log data at the given offset:
    ///  - The first 32 bytes at that offset is the length,
    ///  - Then 'length' bytes of actual data.
    /// Returns an empty byte array if there's not enough data to decode.
    /// </summary>
    public static byte[] ParseBytes(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 32 > data.Length)
        {
            // Offset out of range or not enough for length
            return [];
        }

        int length = (int)new BigInteger(data.Slice(offset, 32).ToArray(), true, true);
        int dataStart = offset + 32;
        int totalNeeded = dataStart + length;

        if (length < 0 || totalNeeded > data.Length)
        {
            // Negative length or out of range
            return [];
        }

        return data.Slice(dataStart, length).ToArray();
    }
}