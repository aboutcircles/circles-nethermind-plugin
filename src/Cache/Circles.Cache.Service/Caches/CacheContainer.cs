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
    public RollbackCache<string, string> V2AvatarToCidMap { get; private set; } = null!;
    public RollbackCache<string, string> V2AvatarToShortNameMap { get; private set; } = null!;

    // Balance Caches (simplified - using string keys for account:token pairs)
    public RollbackCache<string, decimal> V1BalancesByAccountAndToken { get; private set; } = null!;
    public RollbackCache<string, decimal> V2BalancesByAccountAndToken { get; private set; } = null!;
    public RollbackCache<string, long> LastTokenMovement { get; private set; } = null!;

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
        V2AvatarToCidMap,
        V2AvatarToShortNameMap,
        V1BalancesByAccountAndToken,
        V2BalancesByAccountAndToken,
        LastTokenMovement
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
        V2AvatarToCidMap = new RollbackCache<string, string>("V2AvatarToCidMap");
        V2AvatarToShortNameMap = new RollbackCache<string, string>("V2AvatarToShortNameMap");

        // Balance Caches
        V1BalancesByAccountAndToken = new RollbackCache<string, decimal>("V1BalancesByAccountAndToken");
        V2BalancesByAccountAndToken = new RollbackCache<string, decimal>("V2BalancesByAccountAndToken");
        LastTokenMovement = new RollbackCache<string, long>("LastTokenMovement");
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
    /// Gets statistics for all caches.
    /// </summary>
    public Dictionary<string, object> GetStatistics()
    {
        return new Dictionary<string, object>
        {
            ["v1_avatars"] = V1Avatars.Count,
            ["v1_token_owners"] = V1TokenOwnerByToken.Count,
            ["v1_avatar_cids"] = V1AvatarToCidMap.Count,
            ["v2_avatars"] = V2Avatars.Count,
            ["erc20_wrappers"] = Erc20WrapperAddresses.Count,
            ["groups"] = Groups.Count,
            ["v2_avatar_cids"] = V2AvatarToCidMap.Count,
            ["v2_avatar_short_names"] = V2AvatarToShortNameMap.Count,
            ["v1_balances"] = V1BalancesByAccountAndToken.Count,
            ["v2_balances"] = V2BalancesByAccountAndToken.Count,
            ["last_token_movements"] = LastTokenMovement.Count,
            ["total_entries"] = AllCaches.Sum(c => c.Count)
        };
    }
}
