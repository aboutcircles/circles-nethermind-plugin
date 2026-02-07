using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.InvitationAtScale;

public static class Events
{
    // InvitationFarm
    public record AdminSet(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash,
        string Emitter, string NewAdmin) : IIndexEvent;

    public record MaintainerSet(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
        string TransactionHash, string Emitter, string Maintainer) : IIndexEvent;

    public record SeederSet(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash,
        string Emitter, string Seeder) : IIndexEvent;

    public record InviterQuotaUpdated(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
        string TransactionHash, string Emitter, string Inviter, UInt256 Quota) : IIndexEvent;

    public record InvitationModuleUpdated(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
        string TransactionHash, string Emitter, string Module, string GenericCallProxy) : IIndexEvent;

    public record BotCreated(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash,
        string Emitter, string CreatedBot) : IIndexEvent;

    public record InvitesClaimed(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
        string TransactionHash, string Emitter, string Inviter, UInt256 Count) : IIndexEvent;

    public record FarmGrown(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash,
        string Emitter, string Maintainer, UInt256 NumberOfBots, UInt256 TotalNumberOfBots) : IIndexEvent;

    // InvitationModule
    public record RegisterHuman(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
        string TransactionHash, string Emitter, string Human, string OriginInviter, string ProxyInviter) : IIndexEvent;

    // ReferralsModule
    public record AccountCreated(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
        string TransactionHash, string Emitter, string Account) : IIndexEvent;

    public record AccountClaimed(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
        string TransactionHash, string Emitter, string Account) : IIndexEvent;

    // InvitationQuotaGrantModule
    public record QuotaPermissionGranted(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
        string TransactionHash, string Emitter, string Grantee) : IIndexEvent;

    public record QuotaPermissionRevoked(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
        string TransactionHash, string Emitter, string Grantee) : IIndexEvent;

    public record InviterQuotaSet(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
        string TransactionHash, string Emitter, string Grantee, string Inviter, UInt256 Quota) : IIndexEvent;

    public record InviterExtraQuotaAdded(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
        string TransactionHash, string Emitter, string Inviter, UInt256 ExtraQuota) : IIndexEvent;
}