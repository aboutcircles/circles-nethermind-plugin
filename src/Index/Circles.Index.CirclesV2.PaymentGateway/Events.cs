using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.PaymentGateway;

public static class Events
{
    public record GatewayCreated(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Owner,
        string Gateway
    ) : IIndexEvent;

    public record PaymentReceived(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Payer,
        string Payee,
        string Gateway,
        UInt256 TokenId,
        UInt256 Amount,
        byte[] Data
    ) : IIndexEvent;

    public record TrustUpdated(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Gateway,
        string TrustReceiver,
        UInt256 Expiry
    ) : IIndexEvent;
}
