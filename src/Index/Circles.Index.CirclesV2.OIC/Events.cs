using Circles.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.OIC;

public record OpenMiddlewareTransfer(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string OnBehalf,
    string Sender,
    string Recipient,
    UInt256 Amount,
    UInt256 InflationaryAmount,
    byte[] Data
) : IIndexEvent;
