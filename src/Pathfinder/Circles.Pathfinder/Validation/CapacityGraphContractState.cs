using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Validation;

/// <summary>
/// Bridges a runtime CapacityGraph to the IContractState interface
/// so that HubContractValidator can validate paths without knowing
/// about the graph's internal int-based ID system.
/// </summary>
public sealed class CapacityGraphContractState : IContractState
{
    private readonly CapacityGraph _graph;

    public CapacityGraphContractState(CapacityGraph graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    }

    public string? RouterAddress =>
        _graph.RouterNode.HasValue ? AddressIdPool.StringOf(_graph.RouterNode.Value) : null;

    public bool IsGroup(string address)
    {
        var lower = address.ToLowerInvariant();
        // Check if address is in the pool first to avoid creating phantom entries
        if (!AddressIdPool.TryIdOf(lower, out int id))
            return false;
        return _graph.IsGroup(id);
    }

    public bool HasAdvancedUsageFlags(string address)
    {
        var lower = address.ToLowerInvariant();
        if (!AddressIdPool.TryIdOf(lower, out int id))
            return false;
        return _graph.ConsentedAvatars.Contains(id);
    }

    /// <summary>
    /// Hub.sol: isTrusted(truster, circlesId) — does truster accept circlesId's token?
    /// Checks TrustLookup first (avatar trusts from trustQuery.sql).
    /// Falls back to GroupTrustedTokens for group trusters (excluded from TrustLookup
    /// by trustQuery.sql's WHERE group IS NULL filter, stored separately).
    /// Returns true (permissive) if TrustLookup is null to avoid false positives.
    /// </summary>
    public bool IsTrusted(string truster, string circlesId)
    {
        if (_graph.TrustLookup == null)
            return true; // Permissive when no trust data

        var trusterLower = truster.ToLowerInvariant();
        var circlesIdLower = circlesId.ToLowerInvariant();

        if (!AddressIdPool.TryIdOf(trusterLower, out int trusterId))
            return true; // Unknown address — be permissive

        if (!AddressIdPool.TryIdOf(circlesIdLower, out int circlesIdId))
            return true;

        if (_graph.TrustLookup.TryGetValue(trusterId, out var trustedSet))
            return trustedSet.Contains(circlesIdId);

        // Groups are excluded from TrustLookup (trustQuery.sql filters them out).
        // Their trust data lives in GroupTrustedTokens (from groupTrustQuery.sql).
        if (_graph.IsGroup(trusterId))
            return _graph.GroupTrustedTokens.TryGetValue(trusterId, out var groupTrusts)
                   && groupTrusts.Contains(circlesIdId);

        return false; // Truster exists but trusts nothing
    }

    /// <summary>
    /// Hub.sol: avatars[address] != address(0) — is the address registered?
    /// Permissive when RegisteredAvatarIds is empty (synthetic test graphs).
    /// </summary>
    public bool IsRegistered(string address)
    {
        if (_graph.RegisteredAvatarIds.Count == 0)
            return false; // Fail-closed: missing registration data must not silently pass

        var lower = address.ToLowerInvariant();
        if (!AddressIdPool.TryIdOf(lower, out int id))
            return false;

        return _graph.RegisteredAvatarIds.Contains(id);
    }

    public bool IsWrapperToken(string address)
    {
        var lower = address.ToLowerInvariant();
        if (!AddressIdPool.TryIdOf(lower, out int id))
            return false;
        return _graph.WrapperToAvatar.ContainsKey(id);
    }

    public string? ResolveWrapperToAvatar(string wrapperAddress)
    {
        var lower = wrapperAddress.ToLowerInvariant();
        if (!AddressIdPool.TryIdOf(lower, out int wrapperId))
            return null;
        if (!_graph.WrapperToAvatar.TryGetValue(wrapperId, out int avatarId))
            return null;
        return AddressIdPool.StringOf(avatarId);
    }
}
