using Nethermind.Core;
using Nethermind.Int256;

namespace Circles.Index.Common;

/// <summary>
/// Helper methods for decoding standard Solidity event log data
/// with dynamic arrays / bytes.
/// </summary>
public static class LogDataParsingHelper
{
    /// <summary>
    /// Reads a 32-byte offset from <paramref name="data"/> at <paramref name="offsetOfOffset"/>,
    /// interpreting it as a big-endian <see cref="UInt256"/>. Throws if out of range or if
    /// the offset is greater than <see cref="int.MaxValue"/>.
    /// </summary>
    public static int ParseOffset(ReadOnlySpan<byte> data, int offsetOfOffset)
    {
        if (offsetOfOffset < 0 || offsetOfOffset + 32 > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetOfOffset), "Not enough bytes to parse offset.");
        }

        // Interpret the next 32 bytes as a big-endian UInt256
        UInt256 offsetVal = new UInt256(data.Slice(offsetOfOffset, 32), isBigEndian: true);
        return (int)offsetVal;
    }

    /// <summary>
    /// Reads a dynamic array of UInt256 from <paramref name="data"/> at <paramref name="offset"/>:
    /// - the first 32 bytes is the array length (as a UInt256),
    /// - then <c>length * 32</c> bytes of actual data.
    /// Throws if data is insufficient or if <c>length</c> exceeds <see cref="int.MaxValue"/>.
    /// </summary>
    public static UInt256[] ParseUInt256Array(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 32 > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Not enough bytes to parse array length.");
        }

        UInt256 lengthVal = new UInt256(data.Slice(offset, 32), isBigEndian: true);
        int length = (int)lengthVal;
        int arrayStart = offset + 32;
        int totalNeeded = arrayStart + (length * 32);

        if (totalNeeded > data.Length)
        {
            throw new ArgumentException("Not enough bytes for the stated array length.");
        }

        var result = new UInt256[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = new UInt256(data.Slice(arrayStart + i * 32, 32), true);
        }

        return result;
    }

    /// <summary>
    /// Reads a dynamic bytes object from <paramref name="data"/> at <paramref name="offset"/>:
    /// - the first 32 bytes is the length (as a UInt256),
    /// - then <c>length</c> bytes of data.
    /// Throws if data is insufficient or if <c>length</c> exceeds <see cref="int.MaxValue"/>.
    /// </summary>
    public static byte[] ParseBytes(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 32 > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Not enough bytes to parse bytes length.");
        }

        UInt256 lengthVal = new UInt256(data.Slice(offset, 32), isBigEndian: true);
        int length = (int)lengthVal;
        int dataStart = offset + 32;
        int totalNeeded = dataStart + length;

        if (totalNeeded > data.Length)
        {
            throw new ArgumentException("Not enough bytes for the stated bytes length.");
        }

        return data.Slice(dataStart, length).ToArray();
    }

    /// <summary>
    /// Reads exactly 32 bytes from <paramref name="span"/> as a single <see cref="UInt256"/>,
    /// big-endian. Throws if fewer bytes are available.
    /// </summary>
    public static UInt256 ParseSingleUInt256(ReadOnlySpan<byte> span)
    {
        if (span.Length < 32)
        {
            throw new ArgumentException("Insufficient bytes for a single 256-bit value.");
        }

        return new UInt256(span.Slice(0, 32), true);
    }

    /// <summary>
    /// Extracts an EVM address from the end of <paramref name="topicBytes"/> (commonly 32 bytes),
    /// taking the last 20 bytes as the address. Throws if fewer than 20 bytes.
    /// </summary>
    public static string ParseAddressFromTopic(ReadOnlySpan<byte> topicBytes)
    {
        if (topicBytes.Length < 20)
        {
            throw new ArgumentException("Topic bytes must be at least 20 bytes to parse an address.");
        }

        int startIndex = topicBytes.Length - 20;
        ReadOnlySpan<byte> addrSlice = topicBytes.Slice(startIndex, 20);

        return "0x" + BitConverter.ToString(addrSlice.ToArray()).Replace("-", "").ToLowerInvariant();
    }
    
    /// <summary>
    /// Reads a dynamic array of addresses from <paramref name="data"/> at <paramref name="offset"/>.
    /// The first 32 bytes is the array length (as a UInt256). Then each entry is a 32-byte word,
    /// whose last 20 bytes are the address.
    /// </summary>
    public static string[] ParseAddressArray(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 32 > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Not enough bytes to parse address array length.");
        }

        // 1) Read the array length
        UInt256 lengthVal = new UInt256(data.Slice(offset, 32), isBigEndian: true);
        int length = (int)lengthVal;

        // 2) Calculate total needed
        int arrayStart = offset + 32;       // after the length field
        int totalNeeded = arrayStart + length * 32;
        if (totalNeeded > data.Length)
        {
            throw new ArgumentException("Not enough bytes for the stated address array length.");
        }

        var result = new string[length];
        for (int i = 0; i < length; i++)
        {
            // For each element, read 32 bytes => last 20 bytes is the address
            var chunk = data.Slice(arrayStart + i * 32, 32);
            var addressBytes = chunk.Slice(12, 20);
            result[i] = new Address(addressBytes.ToArray()).ToString(true, false);
        }

        return result;
    }
}