using Circles.Common;

namespace Circles.Cache.Service.Caches;

/// <summary>
/// Container for all RollbackCache instances used by the Cache Service.
/// Initialized during warmup and maintained in real-time via pg_notify.
///
/// Uses per-domain ReaderWriterLockSlim to allow concurrent reads (the common case)
/// while serializing writes (once per block event).
/// </summary>
public class CacheContainer : IDisposable
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
    public RollbackCache<string, (string Avatar, CirclesType CirclesType)> Erc20WrapperAddresses { get; private set; } = null!;
    public RollbackCache<string, (string Name, string Mint, string Symbol)> Groups { get; private set; } = null!;
    public RollbackCache<string, (string Member, long ExpiryTime)> GroupMemberships { get; private set; } = null!;
    public RollbackCache<string, string> V2AvatarToCidMap { get; private set; } = null!;
    public RollbackCache<string, string> V2AvatarToShortNameMap { get; private set; } = null!;

    // Balance Caches (simplified - using string keys for account:token pairs)
    public RollbackCache<string, decimal> V1BalancesByAccountAndToken { get; private set; } = null!;
    public RollbackCache<string, decimal> V2BalancesByAccountAndToken { get; private set; } = null!;

    // V2 last activity timestamps for demurrage calculation at query time
    // Key: "account:tokenId" (same as V2BalancesByAccountAndToken), Value: unix timestamp
    public RollbackCache<string, long> V2LastActivity { get; private set; } = null!;

    // Trust Relations Caches (key: "truster:trustee", value: expiryTime)
    public RollbackCache<string, long> V1TrustRelations { get; private set; } = null!;
    public RollbackCache<string, long> V2TrustRelations { get; private set; } = null!;

    // Consented flow flags (key: avatar address, value: flag bytes)
    // Used by the pathfinder graph endpoint to determine consented flow status
    public RollbackCache<string, byte[]> ConsentedFlowFlags { get; private set; } = null!;

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

    // Reverse index for ERC20 wrappers: wrapper address -> (avatar address, wrapper flavor)
    private readonly Dictionary<string, (string Avatar, CirclesType CirclesType)> _erc20WrapperToAvatar = new();

    // Per-domain reader-writer locks: concurrent reads, exclusive writes
    private readonly ReaderWriterLockSlim _balanceLock = new();
    private readonly ReaderWriterLockSlim _trustLock = new();
    private readonly ReaderWriterLockSlim _wrapperLock = new();

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
        V2LastActivity,
        V1TrustRelations,
        V2TrustRelations,
        ConsentedFlowFlags
    };

    private void InitializeCaches()
    {
        // V1 Caches
        V1Avatars = new RollbackCache<string, (string Type, string? TokenAddress)>("V1Avatars", _rollbackCapacity);
        V1TokenOwnerByToken = new RollbackCache<string, string>("V1TokenOwnerByToken", _rollbackCapacity);
        V1AvatarToCidMap = new RollbackCache<string, string>("V1AvatarToCidMap", _rollbackCapacity);
        // V2 Caches
        V2Avatars = new RollbackCache<string, (string Type, long RegisteredAt)>("V2Avatars", _rollbackCapacity);
        Erc20WrapperAddresses = new RollbackCache<string, (string Avatar, CirclesType CirclesType)>("Erc20WrapperAddresses", _rollbackCapacity);
        Groups = new RollbackCache<string, (string Name, string Mint, string Symbol)>("Groups", _rollbackCapacity);
        GroupMemberships = new RollbackCache<string, (string Member, long ExpiryTime)>("GroupMemberships", _rollbackCapacity);
        V2AvatarToCidMap = new RollbackCache<string, string>("V2AvatarToCidMap", _rollbackCapacity);
        V2AvatarToShortNameMap = new RollbackCache<string, string>("V2AvatarToShortNameMap", _rollbackCapacity);

        V1BalancesByAccountAndToken = new RollbackCache<string, decimal>("V1BalancesByAccountAndToken", _rollbackCapacity);
        V2BalancesByAccountAndToken = new RollbackCache<string, decimal>("V2BalancesByAccountAndToken", _rollbackCapacity);
        V2LastActivity = new RollbackCache<string, long>("V2LastActivity", _rollbackCapacity);

        V1TrustRelations = new RollbackCache<string, long>("V1TrustRelations", _rollbackCapacity);
        V2TrustRelations = new RollbackCache<string, long>("V2TrustRelations", _rollbackCapacity);
        ConsentedFlowFlags = new RollbackCache<string, byte[]>("ConsentedFlowFlags", _rollbackCapacity);
    }


    public void RollbackAll(long toBlock)
    {
        foreach (var cache in AllCaches)
        {
            cache.DeleteAllGreaterOrEqualBlock(toBlock);
        }
    }

    public void UpsertV1Trust(long blockNo, string truster, string trustee, long limit)
    {
        var trusterLower = truster.ToLowerInvariant();
        var trusteeLower = trustee.ToLowerInvariant();
        var key = $"{trusterLower}:{trusteeLower}";

        V1TrustRelations.Add(blockNo, key, limit);

        _trustLock.EnterWriteLock();
        try
        {
            AddTrustIndexEntry(_v1TrustsByTruster, trusterLower, key);
            AddTrustIndexEntry(_v1TrustsByTrustee, trusteeLower, key);
        }
        finally
        {
            _trustLock.ExitWriteLock();
        }
    }

    public void RemoveV1Trust(long blockNo, string truster, string trustee)
    {
        var trusterLower = truster.ToLowerInvariant();
        var trusteeLower = trustee.ToLowerInvariant();
        var key = $"{trusterLower}:{trusteeLower}";

        V1TrustRelations.Remove(blockNo, key);

        _trustLock.EnterWriteLock();
        try
        {
            RemoveTrustIndexEntry(_v1TrustsByTruster, trusterLower, key);
            RemoveTrustIndexEntry(_v1TrustsByTrustee, trusteeLower, key);
        }
        finally
        {
            _trustLock.ExitWriteLock();
        }
    }

    public void UpsertV2Trust(long blockNo, string truster, string trustee, long expiryTime)
    {
        var trusterLower = truster.ToLowerInvariant();
        var trusteeLower = trustee.ToLowerInvariant();
        var key = $"{trusterLower}:{trusteeLower}";

        V2TrustRelations.Add(blockNo, key, expiryTime);

        _trustLock.EnterWriteLock();
        try
        {
            AddTrustIndexEntry(_v2TrustsByTruster, trusterLower, key);
            AddTrustIndexEntry(_v2TrustsByTrustee, trusteeLower, key);
        }
        finally
        {
            _trustLock.ExitWriteLock();
        }
    }

    public void RemoveV2Trust(long blockNo, string truster, string trustee)
    {
        var trusterLower = truster.ToLowerInvariant();
        var trusteeLower = trustee.ToLowerInvariant();
        var key = $"{trusterLower}:{trusteeLower}";

        V2TrustRelations.Remove(blockNo, key);

        _trustLock.EnterWriteLock();
        try
        {
            RemoveTrustIndexEntry(_v2TrustsByTruster, trusterLower, key);
            RemoveTrustIndexEntry(_v2TrustsByTrustee, trusteeLower, key);
        }
        finally
        {
            _trustLock.ExitWriteLock();
        }
    }

    public void UpsertGroupMembership(long blockNo, string group, string member, long expiryTime)
    {
        var groupLower = group.ToLowerInvariant();
        var memberLower = member.ToLowerInvariant();
        var key = $"{groupLower}:{memberLower}";

        GroupMemberships.Add(blockNo, key, (memberLower, expiryTime));

        _trustLock.EnterWriteLock();
        try
        {
            AddTrustIndexEntry(_membershipsByGroup, groupLower, key);
            AddTrustIndexEntry(_membershipsByMember, memberLower, key);
        }
        finally
        {
            _trustLock.ExitWriteLock();
        }
    }

    public void RemoveGroupMembership(long blockNo, string group, string member)
    {
        var groupLower = group.ToLowerInvariant();
        var memberLower = member.ToLowerInvariant();
        var key = $"{groupLower}:{memberLower}";

        GroupMemberships.Remove(blockNo, key);

        _trustLock.EnterWriteLock();
        try
        {
            RemoveTrustIndexEntry(_membershipsByGroup, groupLower, key);
            RemoveTrustIndexEntry(_membershipsByMember, memberLower, key);
        }
        finally
        {
            _trustLock.ExitWriteLock();
        }
    }

    public void UpsertWrapper(long blockNo, string wrapperAddress, string avatar, CirclesType circlesType)
    {
        var wrapperLower = wrapperAddress.ToLowerInvariant();
        var avatarLower = avatar.ToLowerInvariant();

        Erc20WrapperAddresses.Add(blockNo, wrapperLower, (avatarLower, circlesType));

        _wrapperLock.EnterWriteLock();
        try
        {
            _erc20WrapperToAvatar[wrapperLower] = (avatarLower, circlesType);
        }
        finally
        {
            _wrapperLock.ExitWriteLock();
        }
    }

    private static void AddTrustIndexEntry(Dictionary<string, HashSet<string>> index, string indexKey, string valueKey)
    {
        if (!index.TryGetValue(indexKey, out var keys))
        {
            keys = new HashSet<string>();
            index[indexKey] = keys;
        }

        keys.Add(valueKey);
    }

    private static void RemoveTrustIndexEntry(Dictionary<string, HashSet<string>> index, string indexKey, string valueKey)
    {
        if (!index.TryGetValue(indexKey, out var keys))
            return;

        keys.Remove(valueKey);
        if (keys.Count == 0)
        {
            index.Remove(indexKey);
        }
    }

    /// <summary>
    /// Updates the secondary indexes when a balance is added or modified.
    /// Call this after updating V1BalancesByAccountAndToken or V2BalancesByAccountAndToken.
    /// </summary>
    public void UpdateBalanceIndex(string accountTokenKey, bool isV1, decimal balance)
    {
        var separatorIndex = accountTokenKey.IndexOf(':');
        if (separatorIndex < 0) return;

        var address = accountTokenKey[..separatorIndex];
        var tokenId = accountTokenKey[(separatorIndex + 1)..];

        _balanceLock.EnterWriteLock();
        try
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
        finally
        {
            _balanceLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets all token IDs for an address from the secondary index (O(1) lookup).
    /// </summary>
    public IEnumerable<string> GetTokenIdsForAddress(string address, bool isV1)
    {
        _balanceLock.EnterReadLock();
        try
        {
            var index = isV1 ? _v1BalancesByAddress : _v2BalancesByAddress;
            return index.TryGetValue(address, out var tokens)
                ? tokens.ToList() // Return copy to avoid lock issues
                : Enumerable.Empty<string>();
        }
        finally
        {
            _balanceLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Rebuilds all secondary indexes from current cache state.
    /// Call this after warmup or after a rollback.
    /// </summary>
    public void RebuildSecondaryIndexes()
    {
        // Acquire all write locks to ensure consistency during full rebuild
        _balanceLock.EnterWriteLock();
        _trustLock.EnterWriteLock();
        _wrapperLock.EnterWriteLock();
        try
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
                var separatorIndex = kvp.Key.IndexOf(':');
                if (separatorIndex >= 0 && kvp.Value > 0)
                {
                    var address = kvp.Key[..separatorIndex];
                    var tokenId = kvp.Key[(separatorIndex + 1)..];

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
                var separatorIndex = kvp.Key.IndexOf(':');
                if (separatorIndex >= 0 && kvp.Value > 0)
                {
                    var address = kvp.Key[..separatorIndex];
                    var tokenId = kvp.Key[(separatorIndex + 1)..];

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
                var separatorIndex = kvp.Key.IndexOf(':');
                if (separatorIndex >= 0)
                {
                    var truster = kvp.Key[..separatorIndex];
                    var trustee = kvp.Key[(separatorIndex + 1)..];

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
                var separatorIndex = kvp.Key.IndexOf(':');
                if (separatorIndex >= 0)
                {
                    var truster = kvp.Key[..separatorIndex];
                    var trustee = kvp.Key[(separatorIndex + 1)..];

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
                var separatorIndex = kvp.Key.IndexOf(':');
                if (separatorIndex >= 0)
                {
                    var group = kvp.Key[..separatorIndex];
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
        finally
        {
            _wrapperLock.ExitWriteLock();
            _trustLock.ExitWriteLock();
            _balanceLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets trust relations where the address is the truster (who they trust).
    /// Returns tuples of (trustee, expiryTime).
    /// </summary>
    public IEnumerable<(string Trustee, long ExpiryTime)> GetTrustsFor(string address, bool isV1)
    {
        var addressLower = address.ToLowerInvariant();
        _trustLock.EnterReadLock();
        try
        {
            var index = isV1 ? _v1TrustsByTruster : _v2TrustsByTruster;
            var cache = isV1 ? V1TrustRelations : V2TrustRelations;

            if (!index.TryGetValue(addressLower, out var keys))
                return Enumerable.Empty<(string, long)>();

            var results = new List<(string, long)>();
            foreach (var key in keys)
            {
                var separatorIndex = key.IndexOf(':');
                if (separatorIndex >= 0 && cache.TryGetValue(key, out var expiryTime))
                {
                    results.Add((key[(separatorIndex + 1)..], expiryTime));
                }
            }
            return results;
        }
        finally
        {
            _trustLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets trust relations where the address is the trustee (who trusts them).
    /// Returns tuples of (truster, expiryTime).
    /// </summary>
    public IEnumerable<(string Truster, long ExpiryTime)> GetTrustedByFor(string address, bool isV1)
    {
        var addressLower = address.ToLowerInvariant();
        _trustLock.EnterReadLock();
        try
        {
            var index = isV1 ? _v1TrustsByTrustee : _v2TrustsByTrustee;
            var cache = isV1 ? V1TrustRelations : V2TrustRelations;

            if (!index.TryGetValue(addressLower, out var keys))
                return Enumerable.Empty<(string, long)>();

            var results = new List<(string, long)>();
            foreach (var key in keys)
            {
                var separatorIndex = key.IndexOf(':');
                if (separatorIndex >= 0 && cache.TryGetValue(key, out var expiryTime))
                {
                    results.Add((key[..separatorIndex], expiryTime));
                }
            }
            return results;
        }
        finally
        {
            _trustLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets all members of a group.
    /// Returns tuples of (member, expiryTime).
    /// </summary>
    public IEnumerable<(string Member, long ExpiryTime)> GetGroupMembers(string groupAddress)
    {
        var groupLower = groupAddress.ToLowerInvariant();
        _trustLock.EnterReadLock();
        try
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
        finally
        {
            _trustLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets all groups a member belongs to.
    /// Returns tuples of (group, expiryTime).
    /// </summary>
    public IEnumerable<(string Group, long ExpiryTime)> GetMemberGroups(string memberAddress)
    {
        var memberLower = memberAddress.ToLowerInvariant();
        _trustLock.EnterReadLock();
        try
        {
            if (!_membershipsByMember.TryGetValue(memberLower, out var keys))
                return Enumerable.Empty<(string, long)>();

            var results = new List<(string, long)>();
            foreach (var key in keys)
            {
                var separatorIndex = key.IndexOf(':');
                if (separatorIndex >= 0 && GroupMemberships.TryGetValue(key, out var membership))
                {
                    results.Add((key[..separatorIndex], membership.ExpiryTime));
                }
            }
            return results;
        }
        finally
        {
            _trustLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets the avatar address that owns an ERC20 wrapper contract.
    /// O(1) lookup using reverse index.
    /// </summary>
    public string? GetAvatarForWrapper(string wrapperAddress)
    {
        var wrapperLower = wrapperAddress.ToLowerInvariant();
        _wrapperLock.EnterReadLock();
        try
        {
            return _erc20WrapperToAvatar.TryGetValue(wrapperLower, out var info) ? info.Avatar : null;
        }
        finally
        {
            _wrapperLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets full wrapper info (avatar address and wrapper flavor) for an ERC20 wrapper contract.
    /// O(1) lookup using reverse index.
    /// </summary>
    public (string Avatar, CirclesType CirclesType)? GetWrapperInfo(string wrapperAddress)
    {
        var wrapperLower = wrapperAddress.ToLowerInvariant();
        _wrapperLock.EnterReadLock();
        try
        {
            return _erc20WrapperToAvatar.TryGetValue(wrapperLower, out var info) ? info : null;
        }
        finally
        {
            _wrapperLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets statistics for all caches.
    /// Each domain's secondary index counts are read under their respective lock,
    /// then combined. The snapshot may be transiently inconsistent across domains,
    /// which is acceptable for metrics.
    /// </summary>
    public Dictionary<string, object> GetStatistics()
    {
        // Read balance-domain stats
        int v1IndexedAddresses, v2IndexedAddresses;
        _balanceLock.EnterReadLock();
        try
        {
            v1IndexedAddresses = _v1BalancesByAddress.Count;
            v2IndexedAddresses = _v2BalancesByAddress.Count;
        }
        finally { _balanceLock.ExitReadLock(); }

        // Read trust-domain stats
        int v1TrustByTruster, v1TrustByTrustee, v2TrustByTruster, v2TrustByTrustee;
        int membershipByGroup, membershipByMember;
        _trustLock.EnterReadLock();
        try
        {
            v1TrustByTruster = _v1TrustsByTruster.Count;
            v1TrustByTrustee = _v1TrustsByTrustee.Count;
            v2TrustByTruster = _v2TrustsByTruster.Count;
            v2TrustByTrustee = _v2TrustsByTrustee.Count;
            membershipByGroup = _membershipsByGroup.Count;
            membershipByMember = _membershipsByMember.Count;
        }
        finally { _trustLock.ExitReadLock(); }

        // Read wrapper-domain stats
        int wrapperReverseIndex;
        _wrapperLock.EnterReadLock();
        try
        {
            wrapperReverseIndex = _erc20WrapperToAvatar.Count;
        }
        finally { _wrapperLock.ExitReadLock(); }

        // Primary caches are ConcurrentDictionary-backed, no lock needed
        return new Dictionary<string, object>
        {
            ["v1_avatars"] = V1Avatars.Count,
            ["v1_token_owners"] = V1TokenOwnerByToken.Count,
            ["v1_avatar_cids"] = V1AvatarToCidMap.Count,
            ["v2_avatars"] = V2Avatars.Count,
            ["erc20_wrappers"] = Erc20WrapperAddresses.Count,
            ["erc20_wrapper_reverse_index"] = wrapperReverseIndex,
            ["groups"] = Groups.Count,
            ["group_memberships"] = GroupMemberships.Count,
            ["group_membership_by_group_index"] = membershipByGroup,
            ["group_membership_by_member_index"] = membershipByMember,
            ["v2_avatar_cids"] = V2AvatarToCidMap.Count,
            ["v2_avatar_short_names"] = V2AvatarToShortNameMap.Count,
            ["v1_balances"] = V1BalancesByAccountAndToken.Count,
            ["v2_balances"] = V2BalancesByAccountAndToken.Count,
            ["v1_trust_relations"] = V1TrustRelations.Count,
            ["v1_trust_by_truster_index"] = v1TrustByTruster,
            ["v1_trust_by_trustee_index"] = v1TrustByTrustee,
            ["v2_trust_relations"] = V2TrustRelations.Count,
            ["v2_trust_by_truster_index"] = v2TrustByTruster,
            ["v2_trust_by_trustee_index"] = v2TrustByTrustee,
            ["total_entries"] = AllCaches.Sum(c => c.Count),
            ["v1_indexed_addresses"] = v1IndexedAddresses,
            ["v2_indexed_addresses"] = v2IndexedAddresses,
            ["v2_last_activity_entries"] = V2LastActivity.Count
        };
    }

    public void Dispose()
    {
        _balanceLock.Dispose();
        _trustLock.Dispose();
        _wrapperLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
