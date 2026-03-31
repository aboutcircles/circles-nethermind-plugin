using Circles.Common;

namespace Circles.Cache.Service.Caches;

/// <summary>
/// Adapts the CacheContainer's avatar and group caches to the
/// <see cref="IRegistrationSet"/> interface used by <see cref="CirclesInvariants"/>.
/// </summary>
public sealed class CacheRegistrationSet : IRegistrationSet
{
    private readonly CacheContainer _caches;

    public CacheRegistrationSet(CacheContainer caches)
    {
        _caches = caches;
    }

    public bool IsRegistered(string address)
    {
        return _caches.V2Avatars.ContainsKey(address)
            || _caches.Groups.ContainsKey(address);
    }

    public bool IsGroup(string address)
    {
        return _caches.Groups.ContainsKey(address);
    }
}

/// <summary>
/// Adapts the CacheContainer's ERC20 wrapper cache to the
/// <see cref="IWrapperLookup"/> interface used by <see cref="CirclesInvariants"/>.
/// </summary>
public sealed class CacheWrapperLookup : IWrapperLookup
{
    private readonly CacheContainer _caches;

    public CacheWrapperLookup(CacheContainer caches)
    {
        _caches = caches;
    }

    public bool TryGetUnderlyingAvatar(string tokenAddress, out string underlyingAvatar)
    {
        // Read from the RollbackCache directly (same data source as BuildBalances wrapper check).
        // The secondary index (_erc20WrapperToAvatar) may not be populated in unit tests that
        // use Erc20WrapperAddresses.Add() directly.
        if (_caches.Erc20WrapperAddresses.TryGetValue(tokenAddress, out var info))
        {
            underlyingAvatar = info.Avatar;
            return true;
        }

        underlyingAvatar = string.Empty;
        return false;
    }
}
