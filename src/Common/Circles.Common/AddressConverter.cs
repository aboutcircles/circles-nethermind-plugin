using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.Common;

public abstract class AddressConverter
{
    public static UInt256 AddressToUInt256(Address address)
    {
        return new(address.Bytes, true);
    }

    public static Address UInt256ToAddress(UInt256 uint256)
    {
        return new Address(uint256.ToBigEndian()[12..].ToHexString());
    }
}

/// <summary>
/// Extension methods for converting Nethermind Address to lowercase hex strings.
/// All addresses stored in the database should use these methods to ensure consistency.
/// </summary>
public static class AddressExtensions
{
    /// <summary>
    /// Converts an Address to a lowercase hex string with 0x prefix.
    /// Use this instead of Address.ToString(true, false) for database storage.
    /// </summary>
    public static string ToLowerHex(this Address address)
    {
        return address.ToString(true, false).ToLowerInvariant();
    }
}