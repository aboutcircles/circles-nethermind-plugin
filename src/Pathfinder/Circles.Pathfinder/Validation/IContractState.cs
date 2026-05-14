namespace Circles.Pathfinder.Validation;

/// <summary>
/// Abstraction over the on-chain state needed by HubContractValidator.
/// Each method mirrors a Hub.sol view/mapping so the validator can check
/// contract rules without depending on CapacityGraph internals.
/// </summary>
public interface IContractState
{
    /// <summary>Hub.isTrusted(truster, circlesId) — does truster accept circlesId's token?</summary>
    bool IsTrusted(string truster, string circlesId);

    /// <summary>Hub.advancedUsageFlags(address) — has the address opted into consented flow?</summary>
    bool HasAdvancedUsageFlags(string address);

    /// <summary>Is the address a registered group in the Hub?</summary>
    bool IsGroup(string address);

    /// <summary>The StandardRouter contract address (null if not set).</summary>
    string? RouterAddress { get; }

    /// <summary>Is the address one of the path mint router contracts known for this graph?</summary>
    bool IsRouter(string address)
    {
        var router = RouterAddress;
        return router != null && string.Equals(router, address, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hub.avatars(address) != address(0) — is the address a registered Circles avatar?
    /// Hub.sol:794-805 checks this for ALL flow vertices; error codes 0x24/0x25.
    /// </summary>
    bool IsRegistered(string address);

    /// <summary>
    /// Is the address a known ERC20 wrapper token contract?
    /// Wrapper tokens have their own contract addresses that are NOT registered avatars.
    /// Hub.sol resolves wrapper token IDs to the underlying avatar for validation.
    /// </summary>
    bool IsWrapperToken(string address);

    /// <summary>
    /// Resolve a wrapper token address to its underlying avatar address.
    /// Returns null if the address is not a wrapper.
    /// </summary>
    string? ResolveWrapperToAvatar(string wrapperAddress);

    /// <summary>
    /// Hub.isApprovedForAll(account, operator) — has the account granted ERC-1155 operator
    /// rights to the given operator? Required for any Hub.operateFlowMatrix call whose
    /// path contains a from-vertex other than the operator/sender.
    /// Returns true (permissive) if no approval data is loaded — keeps unrelated
    /// validation paths from regressing when approvals are not yet indexed.
    /// </summary>
    bool IsApprovedForAll(string account, string @operator) => true;

    /// <summary>
    /// True when this address is a score-group mint router (per-group, set via
    /// ScoreGroupMintRouter at OffchainScoreBasedMintPolicy.initializeGroup).
    /// Used to scope the approveCRC rule so it only fires for score-router edges.
    /// </summary>
    bool IsScoreRouter(string address) => false;

    /// <summary>
    /// circles_getScoreGroupMintLimits view: cached `availableLimit` per
    /// (group, collateral) tuple, mirroring OffchainScoreBasedMintPolicy.sol's
    /// router-branch math: `historicalSupplyOnToday(collateral)
    /// + getMintedAmountOnToday(group, collateral) − HUB.balanceOf(treasury, collateral)`.
    ///
    /// The mapping is keyed on (group, collateral) only — there is no per-intermediary
    /// accounting on-chain — so under the typical "intermediary mints with own token"
    /// pattern, each (group, intermediary-token) row IS effectively per-intermediary;
    /// only in the rare case of two intermediaries supplying the same third-party
    /// collateral do they share a row.
    ///
    /// Returns null when no entry is cached. Rule 12 fails-closed on null when the
    /// edge looks like a score-router→group hop, preventing chain-reverting paths
    /// from leaking through during indexer drift.
    /// </summary>
    long? GetScoreGroupMintLimit(string group, string collateral) => null;
}
