using System.Numerics;
using Nethereum.Util;

namespace Circles.Rpc.Host;

/// <summary>
/// Utilities for encoding/decoding ABI data for ERC-20 and ERC-1155 contract calls.
/// </summary>
public static class AbiEncoder
{
    private static readonly Sha3Keccack _keccak = new();

    /// <summary>
    /// Encodes a call to balanceOf(address) for ERC-20 tokens.
    /// Returns the data field for eth_call.
    /// </summary>
    public static string EncodeBalanceOfErc20(string address)
    {
        // Function signature: balanceOf(address)
        var functionSignature = "balanceOf(address)";
        var methodId = GetMethodId(functionSignature);

        // Remove 0x prefix if present and pad to 32 bytes
        var cleanAddress = address.StartsWith("0x") ? address.Substring(2) : address;
        var paddedAddress = cleanAddress.PadLeft(64, '0');

        return "0x" + methodId + paddedAddress;
    }

    /// <summary>
    /// Encodes a call to balanceOf(address,uint256) for ERC-1155 tokens.
    /// Returns the data field for eth_call.
    /// </summary>
    public static string EncodeBalanceOfErc1155(string address, BigInteger tokenId)
    {
        // Function signature: balanceOf(address,uint256)
        var functionSignature = "balanceOf(address,uint256)";
        var methodId = GetMethodId(functionSignature);

        // Remove 0x prefix if present and pad to 32 bytes
        var cleanAddress = address.StartsWith("0x") ? address.Substring(2) : address;
        var paddedAddress = cleanAddress.PadLeft(64, '0');

        // Convert tokenId to hex and pad to 32 bytes
        var tokenIdHex = tokenId.ToString("X").PadLeft(64, '0');

        return "0x" + methodId + paddedAddress + tokenIdHex;
    }

    /// <summary>
    /// Encodes a call to balanceOfBatch(address[],uint256[]) for ERC-1155 tokens.
    /// Returns the data field for eth_call.
    /// </summary>
    public static string EncodeBalanceOfBatch(string[] addresses, BigInteger[] tokenIds)
    {
        if (addresses.Length != tokenIds.Length)
            throw new ArgumentException("addresses and tokenIds must have the same length");

        // Function signature: balanceOfBatch(address[],uint256[])
        var functionSignature = "balanceOfBatch(address[],uint256[])";
        var methodId = GetMethodId(functionSignature);

        var data = methodId;

        // Offset to addresses array (2 * 32 bytes = 64 bytes, in hex = 128 chars)
        data += "0000000000000000000000000000000000000000000000000000000000000040";

        // Offset to tokenIds array (calculate based on addresses array length)
        var addressesArraySize = 32 + (addresses.Length * 32); // length + data
        var tokenIdsOffset = (64 + addressesArraySize).ToString("X").PadLeft(64, '0');
        data += tokenIdsOffset;

        // Encode addresses array
        data += addresses.Length.ToString("X").PadLeft(64, '0'); // array length
        foreach (var addr in addresses)
        {
            var cleanAddress = addr.StartsWith("0x") ? addr.Substring(2) : addr;
            data += cleanAddress.PadLeft(64, '0');
        }

        // Encode tokenIds array
        data += tokenIds.Length.ToString("X").PadLeft(64, '0'); // array length
        foreach (var tokenId in tokenIds)
        {
            data += tokenId.ToString("X").PadLeft(64, '0');
        }

        return "0x" + data;
    }

    /// <summary>
    /// Decodes a uint256 return value from eth_call.
    /// </summary>
    public static BigInteger DecodeUint256(string hexData)
    {
        if (string.IsNullOrEmpty(hexData))
            return BigInteger.Zero;

        var cleanHex = hexData.StartsWith("0x") ? hexData.Substring(2) : hexData;

        if (string.IsNullOrEmpty(cleanHex))
            return BigInteger.Zero;

        return BigInteger.Parse("0" + cleanHex, System.Globalization.NumberStyles.HexNumber);
    }

    /// <summary>
    /// Decodes a uint256[] return value from eth_call.
    /// </summary>
    public static BigInteger[] DecodeUint256Array(string hexData)
    {
        if (string.IsNullOrEmpty(hexData))
            return Array.Empty<BigInteger>();

        var cleanHex = hexData.StartsWith("0x") ? hexData.Substring(2) : hexData;

        if (cleanHex.Length < 64)
            return Array.Empty<BigInteger>();

        // First 32 bytes = offset to array data
        var offset = int.Parse(cleanHex.Substring(0, 64), System.Globalization.NumberStyles.HexNumber);

        // At offset: first 32 bytes = array length
        var lengthHex = cleanHex.Substring(offset * 2, 64);
        var length = int.Parse(lengthHex, System.Globalization.NumberStyles.HexNumber);

        var result = new BigInteger[length];
        var dataStart = (offset * 2) + 64;

        for (int i = 0; i < length; i++)
        {
            var itemHex = cleanHex.Substring(dataStart + (i * 64), 64);
            result[i] = BigInteger.Parse("0" + itemHex, System.Globalization.NumberStyles.HexNumber);
        }

        return result;
    }

    /// <summary>
    /// Gets the 4-byte method ID for a function signature using Keccak256.
    /// </summary>
    private static string GetMethodId(string functionSignature)
    {
        var hash = _keccak.CalculateHash(functionSignature);
        return hash.Substring(0, 8); // First 4 bytes (8 hex chars)
    }
}
