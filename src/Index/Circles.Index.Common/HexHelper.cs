using Nethermind.Int256;
using Nethereum.Hex.HexConvertors.Extensions;

namespace Circles.Index.Common;

/// <summary>
/// Helper class for hex encoding/decoding using Nethereum.
/// Replaces Nethermind.Core.Extensions.ToHexString to eliminate dependency on Nethermind assemblies.
/// </summary>
public static class HexHelper
{
    /// <summary>
    /// Converts a byte array to a hex string.
    /// </summary>
    /// <param name="bytes">The byte array to convert</param>
    /// <param name="withPrefix">Whether to include the "0x" prefix</param>
    /// <returns>The hex string representation</returns>
    public static string ToHexString(this byte[] bytes, bool withPrefix = true)
    {
        if (bytes == null || bytes.Length == 0)
            return withPrefix ? "0x" : "";

        return withPrefix ? bytes.ToHex(true) : bytes.ToHex(false);
    }

    /// <summary>
    /// Converts a UInt256 to a hex string.
    /// Replaces Nethermind.Core.Extensions.ToHexString for UInt256.
    /// </summary>
    /// <param name="value">The UInt256 value to convert</param>
    /// <param name="withPrefix">Whether to include the "0x" prefix</param>
    /// <returns>The hex string representation</returns>
    public static string ToHexString(this UInt256 value, bool withPrefix = true)
    {
        var bytes = value.ToBigEndian();
        return bytes.ToHexString(withPrefix);
    }

    /// <summary>
    /// Converts a hex string to a byte array.
    /// </summary>
    /// <param name="hex">The hex string (with or without 0x prefix)</param>
    /// <returns>The byte array</returns>
    public static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex))
            return Array.Empty<byte>();

        return hex.HexToByteArray();
    }
}
