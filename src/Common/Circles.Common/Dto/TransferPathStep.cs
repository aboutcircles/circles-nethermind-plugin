namespace Circles.Common.Dto;

/// <summary>
/// A single step in a transitive transfer path. Each step represents one on-chain token transfer.
/// </summary>
public class TransferPathStep
{
    /// <summary>
    /// Sender address for this transfer step (0x-prefixed, lowercase).
    /// </summary>
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// Receiver address for this transfer step (0x-prefixed, lowercase).
    /// </summary>
    public string To { get; set; } = string.Empty;

    /// <summary>
    /// Token owner address identifying which Circles token is transferred (0x-prefixed, lowercase).
    /// </summary>
    public string TokenOwner { get; set; } = string.Empty;

    /// <summary>
    /// Transfer amount in CRC wei (uint256 as decimal string). 1 CRC = 10^18 wei.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}
