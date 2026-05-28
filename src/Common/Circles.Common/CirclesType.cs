namespace Circles.Common;

/// <summary>
/// Wrapper flavor of a <c>CrcV2_ERC20WrapperDeployed</c> token.
/// Underlying value matches the on-chain <c>uint8 circlesType</c> emitted by
/// Hub.sol so the wire format (Postgres column, JSON payload) stays unchanged.
/// </summary>
public enum CirclesType : byte
{
    /// <summary>
    /// Symbol <c>CRC</c> / <c>gCRC</c>. <c>unwrap(uint256)</c> argument is in
    /// demurraged 1155 units; the underlying 1155 transfer is the same amount.
    /// </summary>
    DemurrageCircles = 0,

    /// <summary>
    /// Symbol prefix <c>s-</c>. <c>unwrap(uint256)</c> argument is in inflation-corrected
    /// ERC20 units; the underlying 1155 transfer is <c>amount * γ^day</c>.
    /// </summary>
    InflationaryCircles = 1,
}
