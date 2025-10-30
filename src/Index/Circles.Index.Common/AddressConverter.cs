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