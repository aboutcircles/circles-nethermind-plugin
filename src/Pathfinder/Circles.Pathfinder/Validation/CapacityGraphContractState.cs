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
    /// Maps to CapacityGraph.TrustLookup[trusterId].Contains(circlesIdId).
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

        if (!_graph.TrustLookup.TryGetValue(trusterId, out var trustedSet))
            return false; // Truster exists but trusts nothing

        return trustedSet.Contains(circlesIdId);
    }

    /// <summary>
    /// Hub.sol: avatars[address] != address(0) — is the address registered?
    /// Permissive when RegisteredAvatarIds is empty (synthetic test graphs).
    /// </summary>
    public bool IsRegistered(string address)
    {
        if (_graph.RegisteredAvatarIds.Count == 0)
            return true; // Permissive when no registration data (synthetic graphs)

        var lower = address.ToLowerInvariant();
        if (!AddressIdPool.TryIdOf(lower, out int id))
            return false;

        return _graph.RegisteredAvatarIds.Contains(id);
    }
}
