using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Util;

namespace Circles.Common;

/// <summary>
/// Helper class for computing Keccak256 hashes using Nethereum.
/// Replaces Nethermind.Core.Crypto.Keccak to eliminate dependency on Nethermind assemblies.
/// </summary>
public static class KeccakHelper
{
    private static readonly Sha3Keccack Keccak = new();

    /// <summary>
    /// Computes the Keccak256 hash of the input string.
    /// </summary>
    /// <param name="input">The input string to hash</param>
    /// <returns>The Keccak256 hash as a byte array</returns>
    public static byte[] ComputeHash(string input)
    {
        var hashHex = Keccak.CalculateHash(input);
        return hashHex.HexToByteArray();
    }
}
