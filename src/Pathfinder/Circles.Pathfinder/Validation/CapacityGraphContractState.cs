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

    public bool IsRouter(string address)
    {
        var lower = address.ToLowerInvariant();
        if (!AddressIdPool.TryIdOf(lower, out int id))
            return false;
        return _graph.IsRouter(id);
    }

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

    /// <summary>
    /// Hub.isApprovedForAll(account, operator). Fail-closed when ScoreRouterIds is non-empty —
    /// at least one CrcV2_ScoreGroup.GroupInitialized event has been indexed, so a score
    /// policy is live and missing approval data must be treated as "not indexed yet", not
    /// "no approvals exist". Legacy permissive behavior is preserved only when no score
    /// routers are known to the cache (no policy active or indexer hasn't caught up — the
    /// policy isn't in force regardless).
    ///
    /// DB-source mode: LoadGraph.LoadScoreRouters early-returns on empty ScoreGroupMintPolicies,
    /// so ScoreRouterIds.Count &gt; 0 implies a policy is configured locally.
    /// Cache-source mode: CacheLoadGraph.LoadScoreRouters yields whatever the upstream cache
    /// producer ships, independent of local config. The gate therefore trusts the materialized
    /// cache: if routers are present, treat the policy as in force regardless of local env.
    /// </summary>
    public bool IsApprovedForAll(string account, string @operator)
    {
        var scoreRoutersActive = _graph.ScoreRouterIds.Count > 0;

        if (_graph.OperatorApprovals.Count == 0)
            return !scoreRoutersActive;

        var accountLower = account.ToLowerInvariant();
        var operatorLower = @operator.ToLowerInvariant();

        if (!AddressIdPool.TryIdOf(accountLower, out int accountId))
            return !scoreRoutersActive;

        if (!AddressIdPool.TryIdOf(operatorLower, out int operatorId))
            return false;

        return _graph.OperatorApprovals.TryGetValue(accountId, out var approvedOps)
               && approvedOps.Contains(operatorId);
    }

    /// <summary>
    /// True when the address is a known score-group mint router. Backed by the
    /// CapacityGraph.ScoreRouterIds set, which is populated from
    /// CrcV2_ScoreGroup.GroupInitialized.pathMintRouter — the contract-level source of
    /// truth. Freshly-initialized groups with no mint-limit rows or operator approvals
    /// yet are still classified correctly (both lag the initialize event in practice).
    /// </summary>
    public bool IsScoreRouter(string address)
    {
        var lower = address.ToLowerInvariant();
        if (!AddressIdPool.TryIdOf(lower, out int id))
            return false;
        return _graph.ScoreRouterIds.Contains(id);
    }

    /// <summary>
    /// Read the cached availableLimit for (group, collateral) from
    /// CapacityGraph.ScoreGroupMintLimits, sourced via
    /// circles_getScoreGroupMintLimits → LoadGraph.LoadScoreGroupMintLimits.
    /// Returns null when the tuple has no cached entry (state-snapshot drift,
    /// non-score group, or unknown address) — Rule 12 maps null → fail-closed
    /// for score-router→group edges.
    /// </summary>
    public long? GetScoreGroupMintLimit(string group, string collateral)
    {
        var groupLower = group.ToLowerInvariant();
        var collateralLower = collateral.ToLowerInvariant();

        if (!AddressIdPool.TryIdOf(groupLower, out int groupId))
            return null;
        if (!AddressIdPool.TryIdOf(collateralLower, out int collateralId))
            return null;

        return _graph.ScoreGroupMintLimits.TryGetValue((groupId, collateralId), out var limit)
            ? limit
            : null;
    }
}
