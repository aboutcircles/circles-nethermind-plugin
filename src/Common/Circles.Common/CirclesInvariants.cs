namespace Circles.Common;

/// <summary>
/// Single source of truth for Circles V2 validity predicates.
/// Used by both the Cache Service (output filtering) and Pathfinder (graph construction)
/// to ensure consistent trust, registration, and balance validation.
///
/// These predicates mirror the on-chain invariants enforced by Hub.sol:
/// - All flow vertices must be registered avatars (humans, orgs, or groups)
/// - Trust edges must connect registered avatars with valid expiry
/// - Group trusters are handled separately from avatar trusters
/// - ERC20 wrapper underlying avatars must be registered
/// </summary>
public static class CirclesInvariants
{
    /// <summary>
    /// Checks whether an address is a registered V2 avatar (human, organization, or group).
    /// Hub.sol reverts with CirclesAvatarMustBeRegistered if unregistered addresses
    /// appear as flow vertices.
    /// </summary>
    public static bool IsRegisteredAvatar(string address, IRegistrationSet registrations)
    {
        return registrations.IsRegistered(address);
    }

    /// <summary>
    /// Checks whether a trust edge is valid for pathfinder inclusion.
    /// Implements the filtering logic from trustQuery.sql:
    /// - Both truster and trustee must be registered
    /// - Trust must not be expired
    /// - Group trusters are excluded (handled separately as group trusts)
    /// </summary>
    public static bool IsValidTrustEdge(
        string truster,
        string trustee,
        long expiryTime,
        long currentTimestamp,
        IRegistrationSet registrations)
    {
        // Skip revoked/expired trust (expiryTime == 0 means never-set / indefinite → active)
        if (expiryTime > 0 && expiryTime <= currentTimestamp)
            return false;

        // Both must be registered (humans + orgs + groups)
        if (!registrations.IsRegistered(truster))
            return false;
        if (!registrations.IsRegistered(trustee))
            return false;

        // Group trusters are excluded — they're handled in group trust edges
        if (registrations.IsGroup(truster))
            return false;

        return true;
    }

    /// <summary>
    /// Checks whether a group trust edge is valid for pathfinder inclusion.
    /// Implements the filtering logic from groupTrustQuery.sql:
    /// - Truster must be a registered group using the standard router
    /// - Trusted token (trustee) must be a registered avatar
    /// - Trust must not be expired
    /// </summary>
    public static bool IsValidGroupTrustEdge(
        string groupAddress,
        string trustedToken,
        long expiryTime,
        long currentTimestamp,
        IRegistrationSet registrations,
        IReadOnlySet<string> routerGroups)
    {
        // Truster must be a router-filtered group
        if (!routerGroups.Contains(groupAddress))
            return false;

        // Skip revoked/expired trust
        if (expiryTime > 0 && expiryTime <= currentTimestamp)
            return false;

        // Trusted token must be a registered avatar (human, org, or group)
        if (!registrations.IsRegistered(trustedToken))
            return false;

        return true;
    }

    /// <summary>
    /// Checks whether a V2 balance entry is valid for pathfinder inclusion.
    /// Implements the filtering logic from balanceQuery.sql:
    /// - Account must be a registered avatar
    /// - For native ERC1155: tokenAddress (= token owner) must be registered
    /// - For wrappers: underlying avatar must be registered
    /// </summary>
    public static bool IsValidBalance(
        string account,
        string tokenAddress,
        IRegistrationSet registrations,
        IWrapperLookup? wrapperLookup)
    {
        // Account must be a registered avatar
        if (!registrations.IsRegistered(account))
            return false;

        // Check token validity
        if (wrapperLookup != null && wrapperLookup.TryGetUnderlyingAvatar(tokenAddress, out var underlyingAvatar))
        {
            // Wrapper token: underlying avatar must be registered
            if (!registrations.IsRegistered(underlyingAvatar))
                return false;
        }
        else
        {
            // Native ERC1155: tokenAddress IS the token owner — must be registered
            if (!registrations.IsRegistered(tokenAddress))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks whether a wrapper mapping is valid for pathfinder inclusion.
    /// Implements the filtering logic from wrapperMappingQuery.sql:
    /// - Underlying avatar must be registered
    /// </summary>
    public static bool IsValidWrapperMapping(
        string underlyingAvatar,
        IRegistrationSet registrations)
    {
        return registrations.IsRegistered(underlyingAvatar);
    }

    /// <summary>
    /// Checks whether a group membership is valid.
    /// - Group must be registered
    /// - Member must be a registered avatar
    /// - Membership must not be expired
    /// </summary>
    public static bool IsValidGroupMembership(
        string groupAddress,
        string member,
        long expiryTime,
        long currentTimestamp,
        IRegistrationSet registrations)
    {
        // Must not be expired
        if (expiryTime > 0 && expiryTime <= currentTimestamp)
            return false;

        // Group must be registered as a group
        if (!registrations.IsGroup(groupAddress))
            return false;

        // Member must be a registered avatar
        if (!registrations.IsRegistered(member))
            return false;

        return true;
    }

    /// <summary>
    /// Checks whether a consented flow flag is valid for pathfinder inclusion.
    /// - Avatar must be registered
    /// </summary>
    public static bool IsValidConsentedFlowFlag(
        string avatar,
        IRegistrationSet registrations)
    {
        return registrations.IsRegistered(avatar);
    }
}

/// <summary>
/// Abstraction for checking avatar registration status.
/// Implemented by CacheRegistrationSet (cache service) and pathfinder's own set.
/// </summary>
public interface IRegistrationSet
{
    /// <summary>Returns true if the address is a registered V2 avatar (human, org, or group).</summary>
    bool IsRegistered(string address);

    /// <summary>Returns true if the address is a registered V2 group.</summary>
    bool IsGroup(string address);
}

/// <summary>
/// Abstraction for wrapper address → underlying avatar lookups.
/// Implemented differently by cache (CacheContainer) and pathfinder (CapacityGraph).
/// </summary>
public interface IWrapperLookup
{
    /// <summary>
    /// If tokenAddress is an ERC20 wrapper, returns the underlying avatar address.
    /// Returns false for native ERC1155 token addresses.
    /// </summary>
    bool TryGetUnderlyingAvatar(string tokenAddress, out string underlyingAvatar);
}

/// <summary>
/// Simple implementation of <see cref="IRegistrationSet"/> backed by two HashSets.
/// </summary>
public sealed class HashSetRegistrationSet : IRegistrationSet
{
    private readonly IReadOnlySet<string> _avatars;
    private readonly IReadOnlySet<string> _groups;

    /// <param name="avatars">All registered addresses (humans + orgs + groups).</param>
    /// <param name="groups">Only group addresses (subset of avatars).</param>
    public HashSetRegistrationSet(IReadOnlySet<string> avatars, IReadOnlySet<string> groups)
    {
        _avatars = avatars;
        _groups = groups;
    }

    public bool IsRegistered(string address) => _avatars.Contains(address) || _groups.Contains(address);
    public bool IsGroup(string address) => _groups.Contains(address);
}
