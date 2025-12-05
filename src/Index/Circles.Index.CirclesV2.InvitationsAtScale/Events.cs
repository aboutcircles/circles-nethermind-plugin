using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.InvitationsAtScale;

public static class Events
{
    // ============================================
    // InvitationModule Events
    // ============================================

    /// <summary>
    /// Emitted when an invitee is registered as a human through the InvitationModule.
    /// </summary>
    public record RegisterHuman(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Human,
        string OriginInviter,
        string ProxyInviter
    ) : IIndexEvent;

    // ============================================
    // ReferralsModule Events
    // ============================================

    /// <summary>
    /// Emitted when a pre-made Safe is deployed for an origin inviter's signer.
    /// </summary>
    public record AccountCreated(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Account
    ) : IIndexEvent;

    /// <summary>
    /// Emitted after a successful human claim when the module disables itself on the Safe.
    /// </summary>
    public record AccountClaimed(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Account
    ) : IIndexEvent;

    // ============================================
    // InvitationFarm Events
    // ============================================

    /// <summary>
    /// Emitted when the admin address is updated.
    /// </summary>
    public record AdminSet(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string NewAdmin
    ) : IIndexEvent;

    /// <summary>
    /// Emitted when the maintainer address is updated.
    /// </summary>
    public record MaintainerSet(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Maintainer
    ) : IIndexEvent;

    /// <summary>
    /// Emitted when the seeder address is updated.
    /// </summary>
    public record SeederSet(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Seeder
    ) : IIndexEvent;

    /// <summary>
    /// Emitted when an inviter's quota is set/updated.
    /// </summary>
    public record InviterQuotaUpdated(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Inviter,
        UInt256 Quota
    ) : IIndexEvent;

    /// <summary>
    /// Emitted when the Invitation Module (and its proxy) is updated.
    /// </summary>
    public record InvitationModuleUpdated(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Module,
        string GenericCallProxy
    ) : IIndexEvent;

    /// <summary>
    /// Emitted after a new InvitationBot is created and added to the list.
    /// </summary>
    public record BotCreated(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string CreatedBot
    ) : IIndexEvent;

    /// <summary>
    /// Emitted after invites are successfully claimed.
    /// </summary>
    public record InvitesClaimed(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Inviter,
        UInt256 Count
    ) : IIndexEvent;

    /// <summary>
    /// Emitted when the farm is grown via maintainer flow.
    /// </summary>
    public record FarmGrown(
        long BlockNumber,
        long Timestamp,
        int TransactionIndex,
        int LogIndex,
        string TransactionHash,
        string Emitter,
        string Maintainer,
        UInt256 NumberOfBots,
        UInt256 TotalNumberOfBots
    ) : IIndexEvent;
}
