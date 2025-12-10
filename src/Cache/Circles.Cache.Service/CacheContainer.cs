using Circles.Index.Common;

namespace Circles.Cache.Service.Caches;

/// <summary>
/// Container for all RollbackCache instances used by the Cache Service.
/// Initialized during warmup and maintained in real-time via pg_notify.
///
/// NOTE: This is a simplified initial implementation. Full implementation will include:
/// - V1/V2 Avatar caches
/// - Token owner mappings
/// - Erc20 wrapper addresses
/// - Groups
/// - Balance caches
/// - Name registry mappings
/// </summary>
public class CacheContainer
{
    private readonly int _rollbackCapacity;

    public CacheContainer(int rollbackCapacity)
    {
        _rollbackCapacity = rollbackCapacity;
        InitializeCaches();
    }

    // V1 Caches (simplified - using string tuples for now)
    public RollbackCache<string, (string Type, string? TokenAddress)> V1Avatars { get; private set; } = null!;
    public RollbackCache<string, string> V1TokenOwnerByToken { get; private set; } = null!;
    public RollbackCache<string, string> V1AvatarToCidMap { get; private set; } = null!;

    public RollbackCache<string, (string Type, long RegisteredAt)> V2Avatars { get; private set; } = null!;
    public RollbackCache<string, (string Avatar, int CirclesType)> Erc20WrapperAddresses { get; private set; } = null!;
    public RollbackCache<string, (string Name, string Mint, string Symbol)> Groups { get; private set; } = null!;
    public RollbackCache<string, (string Member, long ExpiryTime)> GroupMemberships { get; private set; } = null!;
    public RollbackCache<string, string> V2AvatarToCidMap { get; private set; } = null!;
    public RollbackCache<string, string> V2AvatarToShortNameMap { get; private set; } = null!;

    // Balance Caches (simplified - using string keys for account:token pairs)
    public RollbackCache<string, decimal> V1BalancesByAccountAndToken { get; private set; } = null!;
    public RollbackCache<string, decimal> V2BalancesByAccountAndToken { get; private set; } = null!;

    // Trust Relations Caches (key: "truster:trustee", value: expiryTime)
    public RollbackCache<string, long> V1TrustRelations { get; private set; } = null!;
    public RollbackCache<string, long> V2TrustRelations { get; private set; } = null!;

    // Secondary Indexes for O(1) balance lookups
    // Maps address -> set of token IDs that address holds
    private readonly Dictionary<string, HashSet<string>> _v1BalancesByAddress = new();
    private readonly Dictionary<string, HashSet<string>> _v2BalancesByAddress = new();

    // Secondary Indexes for trust relations
    // Maps address -> set of "truster:trustee" keys where address is the truster
    private readonly Dictionary<string, HashSet<string>> _v1TrustsByTruster = new();
    private readonly Dictionary<string, HashSet<string>> _v1TrustsByTrustee = new();
    private readonly Dictionary<string, HashSet<string>> _v2TrustsByTruster = new();
    private readonly Dictionary<string, HashSet<string>> _v2TrustsByTrustee = new();

    // Secondary Indexes for group memberships
    // Maps group -> set of "group:member" keys
    private readonly Dictionary<string, HashSet<string>> _membershipsByGroup = new();
    // Maps member -> set of "group:member" keys
    private readonly Dictionary<string, HashSet<string>> _membershipsByMember = new();

    // Reverse index for ERC20 wrappers: wrapper address -> (avatar address, circlesType)
    // circlesType: 0 = demurraged, 1 = inflationary
    private readonly Dictionary<string, (string Avatar, int CirclesType)> _erc20WrapperToAvatar = new();

    private readonly object _indexLock = new();

    /// <summary>
    /// Gets all caches as an enumerable for bulk operations (e.g., rollback).
    /// </summary>
    public IEnumerable<IRollbackCache> AllCaches => new IRollbackCache[]
    {
        V1Avatars,
        V1TokenOwnerByToken,
        V1AvatarToCidMap,
        V2Avatars,
        Erc20WrapperAddresses,
        Groups,
        GroupMemberships,
        V2AvatarToCidMap,
        V2AvatarToShortNameMap,
        V1BalancesByAccountAndToken,
        V2BalancesByAccountAndToken,
        V1TrustRelations,
        V2TrustRelations
    };

    private void InitializeCaches()
    {
        // V1 Caches
        V1Avatars = new RollbackCache<string, (string Type, string? TokenAddress)>("V1Avatars");
        V1TokenOwnerByToken = new RollbackCache<string, string>("V1TokenOwnerByToken");
        V1AvatarToCidMap = new RollbackCache<string, string>("V1AvatarToCidMap");
        // V2 Caches
        V2Avatars = new RollbackCache<string, (string Type, long RegisteredAt)>("V2Avatars");
        Erc20WrapperAddresses = new RollbackCache<string, (string Avatar, int CirclesType)>("Erc20WrapperAddresses");
        Groups = new RollbackCache<string, (string Name, string Mint, string Symbol)>("Groups");
        GroupMemberships = new RollbackCache<string, (string Member, long ExpiryTime)>("GroupMemberships");
        V2AvatarToCidMap = new RollbackCache<string, string>("V2AvatarToCidMap");
        V2AvatarToShortNameMap = new RollbackCache<string, string>("V2AvatarToShortNameMap");

        V1BalancesByAccountAndToken = new RollbackCache<string, decimal>("V1BalancesByAccountAndToken");
        V2BalancesByAccountAndToken = new RollbackCache<string, decimal>("V2BalancesByAccountAndToken");


        V1TrustRelations = new RollbackCache<string, long>("V1TrustRelations");
        V2TrustRelations = new RollbackCache<string, long>("V2TrustRelations");
    }


    public void RollbackAll(long toBlock)
    {
        foreach (var cache in AllCaches)
        {
            cache.DeleteAllGreaterOrEqualBlock(toBlock);
        }
    }

    /// <summary>
    /// Updates the secondary indexes when a balance is added or modified.
    /// Call this after updating V1BalancesByAccountAndToken or V2BalancesByAccountAndToken.
    /// </summary>
    public void UpdateBalanceIndex(string accountTokenKey, bool isV1, decimal balance)
    {
        // Key format is "address:tokenId"
        var parts = accountTokenKey.Split(':', 2);
        if (parts.Length != 2) return;

        var address = parts[0];
        var tokenId = parts[1];

        lock (_indexLock)
        {
            var index = isV1 ? _v1BalancesByAddress : _v2BalancesByAddress;

            if (balance > 0)
            {
                // Add to index
                if (!index.TryGetValue(address, out var tokens))
                {
                    tokens = new HashSet<string>();
                    index[address] = tokens;
                }
                tokens.Add(tokenId);
            }
            else
            {
                // Remove from index if balance is zero
                if (index.TryGetValue(address, out var tokens))
                {
                    tokens.Remove(tokenId);
                    if (tokens.Count == 0)
                    {
                        index.Remove(address);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets all token IDs for an address from the secondary index (O(1) lookup).
    /// </summary>
    public IEnumerable<string> GetTokenIdsForAddress(string address, bool isV1)
    {
        lock (_indexLock)
        {
            var index = isV1 ? _v1BalancesByAddress : _v2BalancesByAddress;
            return index.TryGetValue(address, out var tokens)
                ? tokens.ToList() // Return copy to avoid lock issues
                : Enumerable.Empty<string>();
        }
    }

    /// <summary>
    /// Rebuilds all secondary indexes from current cache state.
    /// Call this after warmup or after a rollback.
    /// </summary>
    public void RebuildSecondaryIndexes()
    {
        lock (_indexLock)
        {
            _v1BalancesByAddress.Clear();
            _v2BalancesByAddress.Clear();
            _v1TrustsByTruster.Clear();
            _v1TrustsByTrustee.Clear();
            _v2TrustsByTruster.Clear();
            _v2TrustsByTrustee.Clear();
            _membershipsByGroup.Clear();
            _membershipsByMember.Clear();
            _erc20WrapperToAvatar.Clear();

            // Rebuild V1 balance index
            foreach (var kvp in V1BalancesByAccountAndToken.ReadOnlyDictionary)
            {
                var parts = kvp.Key.Split(':', 2);
                if (parts.Length == 2 && kvp.Value > 0)
                {
                    var address = parts[0];
                    var tokenId = parts[1];

                    if (!_v1BalancesByAddress.TryGetValue(address, out var tokens))
                    {
                        tokens = new HashSet<string>();
                        _v1BalancesByAddress[address] = tokens;
                    }
                    tokens.Add(tokenId);
                }
            }

            // Rebuild V2 balance index
            foreach (var kvp in V2BalancesByAccountAndToken.ReadOnlyDictionary)
            {
                var parts = kvp.Key.Split(':', 2);
                if (parts.Length == 2 && kvp.Value > 0)
                {
                    var address = parts[0];
                    var tokenId = parts[1];

                    if (!_v2BalancesByAddress.TryGetValue(address, out var tokens))
                    {
                        tokens = new HashSet<string>();
                        _v2BalancesByAddress[address] = tokens;
                    }
                    tokens.Add(tokenId);
                }
            }

            // Rebuild V1 trust relation indexes
            foreach (var kvp in V1TrustRelations.ReadOnlyDictionary)
            {
                var parts = kvp.Key.Split(':', 2);
                if (parts.Length == 2)
                {
                    var truster = parts[0];
                    var trustee = parts[1];

                    if (!_v1TrustsByTruster.TryGetValue(truster, out var trusterKeys))
                    {
                        trusterKeys = new HashSet<string>();
                        _v1TrustsByTruster[truster] = trusterKeys;
                    }
                    trusterKeys.Add(kvp.Key);

                    if (!_v1TrustsByTrustee.TryGetValue(trustee, out var trusteeKeys))
                    {
                        trusteeKeys = new HashSet<string>();
                        _v1TrustsByTrustee[trustee] = trusteeKeys;
                    }
                    trusteeKeys.Add(kvp.Key);
                }
            }

            // Rebuild V2 trust relation indexes
            foreach (var kvp in V2TrustRelations.ReadOnlyDictionary)
            {
                var parts = kvp.Key.Split(':', 2);
                if (parts.Length == 2)
                {
                    var truster = parts[0];
                    var trustee = parts[1];

                    if (!_v2TrustsByTruster.TryGetValue(truster, out var trusterKeys))
                    {
                        trusterKeys = new HashSet<string>();
                        _v2TrustsByTruster[truster] = trusterKeys;
                    }
                    trusterKeys.Add(kvp.Key);

                    if (!_v2TrustsByTrustee.TryGetValue(trustee, out var trusteeKeys))
                    {
                        trusteeKeys = new HashSet<string>();
                        _v2TrustsByTrustee[trustee] = trusteeKeys;
                    }
                    trusteeKeys.Add(kvp.Key);
                }
            }

            // Rebuild group membership indexes
            foreach (var kvp in GroupMemberships.ReadOnlyDictionary)
            {
                var parts = kvp.Key.Split(':', 2);
                if (parts.Length == 2)
                {
                    var group = parts[0];
                    var member = kvp.Value.Member.ToLowerInvariant();

                    if (!_membershipsByGroup.TryGetValue(group, out var groupKeys))
                    {
                        groupKeys = new HashSet<string>();
                        _membershipsByGroup[group] = groupKeys;
                    }
                    groupKeys.Add(kvp.Key);

                    if (!_membershipsByMember.TryGetValue(member, out var memberKeys))
                    {
                        memberKeys = new HashSet<string>();
                        _membershipsByMember[member] = memberKeys;
                    }
                    memberKeys.Add(kvp.Key);
                }
            }

            // Copy ERC20 wrapper data to secondary index for direct access
            // Erc20WrapperAddresses is now keyed by wrapper address, so we just copy it
            foreach (var kvp in Erc20WrapperAddresses.ReadOnlyDictionary)
            {
                var wrapper = kvp.Key; // Already lowercase
                var avatar = kvp.Value.Avatar;
                var circlesType = kvp.Value.CirclesType;
                _erc20WrapperToAvatar[wrapper] = (avatar, circlesType);
            }
        }
    }

    /// <summary>
    /// Gets trust relations where the address is the truster (who they trust).
    /// Returns tuples of (trustee, expiryTime).
    /// </summary>
    public IEnumerable<(string Trustee, long ExpiryTime)> GetTrustsFor(string address, bool isV1)
    {
        var addressLower = address.ToLowerInvariant();
        lock (_indexLock)
        {
            var index = isV1 ? _v1TrustsByTruster : _v2TrustsByTruster;
            var cache = isV1 ? V1TrustRelations : V2TrustRelations;

            if (!index.TryGetValue(addressLower, out var keys))
                return Enumerable.Empty<(string, long)>();

            var results = new List<(string, long)>();
            foreach (var key in keys)
            {
                var parts = key.Split(':', 2);
                if (parts.Length == 2 && cache.TryGetValue(key, out var expiryTime))
                {
                    results.Add((parts[1], expiryTime));
                }
            }
            return results;
        }
    }

    /// <summary>
    /// Gets trust relations where the address is the trustee (who trusts them).
    /// Returns tuples of (truster, expiryTime).
    /// </summary>
    public IEnumerable<(string Truster, long ExpiryTime)> GetTrustedByFor(string address, bool isV1)
    {
        var addressLower = address.ToLowerInvariant();
        lock (_indexLock)
        {
            var index = isV1 ? _v1TrustsByTrustee : _v2TrustsByTrustee;
            var cache = isV1 ? V1TrustRelations : V2TrustRelations;

            if (!index.TryGetValue(addressLower, out var keys))
                return Enumerable.Empty<(string, long)>();

            var results = new List<(string, long)>();
            foreach (var key in keys)
            {
                var parts = key.Split(':', 2);
                if (parts.Length == 2 && cache.TryGetValue(key, out var expiryTime))
                {
                    results.Add((parts[0], expiryTime));
                }
            }
            return results;
        }
    }

    /// <summary>
    /// Gets all members of a group.
    /// Returns tuples of (member, expiryTime).
    /// </summary>
    public IEnumerable<(string Member, long ExpiryTime)> GetGroupMembers(string groupAddress)
    {
        var groupLower = groupAddress.ToLowerInvariant();
        lock (_indexLock)
        {
            if (!_membershipsByGroup.TryGetValue(groupLower, out var keys))
                return Enumerable.Empty<(string, long)>();

            var results = new List<(string, long)>();
            foreach (var key in keys)
            {
                if (GroupMemberships.TryGetValue(key, out var membership))
                {
                    results.Add((membership.Member, membership.ExpiryTime));
                }
            }
            return results;
        }
    }

    /// <summary>
    /// Gets all groups a member belongs to.
    /// Returns tuples of (group, expiryTime).
    /// </summary>
    public IEnumerable<(string Group, long ExpiryTime)> GetMemberGroups(string memberAddress)
    {
        var memberLower = memberAddress.ToLowerInvariant();
        lock (_indexLock)
        {
            if (!_membershipsByMember.TryGetValue(memberLower, out var keys))
                return Enumerable.Empty<(string, long)>();

            var results = new List<(string, long)>();
            foreach (var key in keys)
            {
                var parts = key.Split(':', 2);
                if (parts.Length == 2 && GroupMemberships.TryGetValue(key, out var membership))
                {
                    results.Add((parts[0], membership.ExpiryTime));
                }
            }
            return results;
        }
    }

    /// <summary>
    /// Gets the avatar address that owns an ERC20 wrapper contract.
    /// O(1) lookup using reverse index.
    /// </summary>
    public string? GetAvatarForWrapper(string wrapperAddress)
    {
        var wrapperLower = wrapperAddress.ToLowerInvariant();
        lock (_indexLock)
        {
            return _erc20WrapperToAvatar.TryGetValue(wrapperLower, out var info) ? info.Avatar : null;
        }
    }

    /// <summary>
    /// Gets full wrapper info (avatar address and circlesType) for an ERC20 wrapper contract.
    /// circlesType: 0 = demurraged, 1 = inflationary
    /// O(1) lookup using reverse index.
    /// </summary>
    public (string Avatar, int CirclesType)? GetWrapperInfo(string wrapperAddress)
    {
        var wrapperLower = wrapperAddress.ToLowerInvariant();
        lock (_indexLock)
        {
            return _erc20WrapperToAvatar.TryGetValue(wrapperLower, out var info) ? info : null;
        }
    }

    /// <summary>
    /// Gets statistics for all caches.
    /// </summary>
    public Dictionary<string, object> GetStatistics()
    {
        lock (_indexLock)
        {
            return new Dictionary<string, object>
            {
                ["v1_avatars"] = V1Avatars.Count,
                ["v1_token_owners"] = V1TokenOwnerByToken.Count,
                ["v1_avatar_cids"] = V1AvatarToCidMap.Count,
                ["v2_avatars"] = V2Avatars.Count,
                ["erc20_wrappers"] = Erc20WrapperAddresses.Count,
                ["erc20_wrapper_reverse_index"] = _erc20WrapperToAvatar.Count,
                ["groups"] = Groups.Count,
                ["group_memberships"] = GroupMemberships.Count,
                ["group_membership_by_group_index"] = _membershipsByGroup.Count,
                ["group_membership_by_member_index"] = _membershipsByMember.Count,
                ["v2_avatar_cids"] = V2AvatarToCidMap.Count,
                ["v2_avatar_short_names"] = V2AvatarToShortNameMap.Count,
                ["v1_balances"] = V1BalancesByAccountAndToken.Count,
                ["v2_balances"] = V2BalancesByAccountAndToken.Count,
                ["v1_trust_relations"] = V1TrustRelations.Count,
                ["v1_trust_by_truster_index"] = _v1TrustsByTruster.Count,
                ["v1_trust_by_trustee_index"] = _v1TrustsByTrustee.Count,
                ["v2_trust_relations"] = V2TrustRelations.Count,
                ["v2_trust_by_truster_index"] = _v2TrustsByTruster.Count,
                ["v2_trust_by_trustee_index"] = _v2TrustsByTrustee.Count,
                ["total_entries"] = AllCaches.Sum(c => c.Count),
                ["v1_indexed_addresses"] = _v1BalancesByAddress.Count,
                ["v2_indexed_addresses"] = _v2BalancesByAddress.Count
            };
        }
    }
}
