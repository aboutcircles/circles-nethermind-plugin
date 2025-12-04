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

    // V2 Caches (simplified - using string tuples for now)
    public RollbackCache<string, (string Type, long RegisteredAt)> V2Avatars { get; private set; } = null!;
    public RollbackCache<string, string> Erc20WrapperAddresses { get; private set; } = null!;
    public RollbackCache<string, (string Name, string Mint)> Groups { get; private set; } = null!;
    public RollbackCache<string, (string Member, long ExpiryTime)> GroupMemberships { get; private set; } = null!;
    public RollbackCache<string, string> V2AvatarToCidMap { get; private set; } = null!;
    public RollbackCache<string, string> V2AvatarToShortNameMap { get; private set; } = null!;

    // Balance Caches (simplified - using string keys for account:token pairs)
    public RollbackCache<string, decimal> V1BalancesByAccountAndToken { get; private set; } = null!;
    public RollbackCache<string, decimal> V2BalancesByAccountAndToken { get; private set; } = null!;
    public RollbackCache<string, long> LastTokenMovement { get; private set; } = null!;

    // Trust Relations Caches (key: "truster:trustee", value: expiryTime)
    public RollbackCache<string, long> V1TrustRelations { get; private set; } = null!;
    public RollbackCache<string, long> V2TrustRelations { get; private set; } = null!;

    // Secondary Indexes for O(1) balance lookups
    // Maps address -> set of token IDs that address holds
    private readonly Dictionary<string, HashSet<string>> _v1BalancesByAddress = new();
    private readonly Dictionary<string, HashSet<string>> _v2BalancesByAddress = new();
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
        LastTokenMovement,
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
        Erc20WrapperAddresses = new RollbackCache<string, string>("Erc20WrapperAddresses");
        Groups = new RollbackCache<string, (string Name, string Mint)>("Groups");
        GroupMemberships = new RollbackCache<string, (string Member, long ExpiryTime)>("GroupMemberships");
        V2AvatarToCidMap = new RollbackCache<string, string>("V2AvatarToCidMap");
        V2AvatarToShortNameMap = new RollbackCache<string, string>("V2AvatarToShortNameMap");

        // Balance Caches
        V1BalancesByAccountAndToken = new RollbackCache<string, decimal>("V1BalancesByAccountAndToken");
        V2BalancesByAccountAndToken = new RollbackCache<string, decimal>("V2BalancesByAccountAndToken");
        LastTokenMovement = new RollbackCache<string, long>("LastTokenMovement");

        // Trust Relations Caches
        V1TrustRelations = new RollbackCache<string, long>("V1TrustRelations");
        V2TrustRelations = new RollbackCache<string, long>("V2TrustRelations");
    }

    /// <summary>
    /// Rolls back all caches to the specified block number.
    /// </summary>
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

            // Rebuild V1 index
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

            // Rebuild V2 index
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
                ["groups"] = Groups.Count,
                ["group_memberships"] = GroupMemberships.Count,
                ["v2_avatar_cids"] = V2AvatarToCidMap.Count,
                ["v2_avatar_short_names"] = V2AvatarToShortNameMap.Count,
                ["v1_balances"] = V1BalancesByAccountAndToken.Count,
                ["v2_balances"] = V2BalancesByAccountAndToken.Count,
                ["last_token_movements"] = LastTokenMovement.Count,
                ["v1_trust_relations"] = V1TrustRelations.Count,
                ["v2_trust_relations"] = V2TrustRelations.Count,
                ["total_entries"] = AllCaches.Sum(c => c.Count),
                ["v1_indexed_addresses"] = _v1BalancesByAddress.Count,
                ["v2_indexed_addresses"] = _v2BalancesByAddress.Count
            };
        }
    }
}
