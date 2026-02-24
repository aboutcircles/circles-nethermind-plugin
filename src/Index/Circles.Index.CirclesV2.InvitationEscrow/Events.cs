using Circles.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.InvitationEscrow;

public static class Events
{
    public record InvitationEscrowed(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Inviter,
        string Invitee,
        UInt256 Amount
    ) : IIndexEvent;

    public record InvitationRedeemed(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Inviter,
        string Invitee,
        UInt256 Amount
    ) : IIndexEvent;

    public record InvitationRefunded(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Inviter,
        string Invitee,
        UInt256 Amount
    ) : IIndexEvent;

    public record InvitationRevoked(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Inviter,
        string Invitee,
        UInt256 Amount
    ) : IIndexEvent;
}
