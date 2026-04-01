using Circles.Pathfinder.Data;
using Nethermind.Int256;

namespace Circles.Pathfinder.Tests.Helpers;

/// <summary>
/// Mock ILoadGraph for unit testing GraphFactory and pathfinding logic.
/// Allows programmatic construction of test graphs without database dependencies.
/// </summary>
public class MockLoadGraph : ILoadGraph
{
    private readonly List<(string Truster, string Trustee, int Limit)> _trusts = new();
    private readonly List<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> _balances = new();
    private readonly List<string> _groups = new();
    private readonly List<(string GroupAddress, string TrustedToken)> _groupTrusts = new();
    private readonly List<(string Avatar, bool HasConsentedFlow)> _consentedFlags = new();
    private readonly List<string> _registeredAvatars = new();
    private readonly List<(string WrapperAddress, string UnderlyingAvatar)> _wrapperMappings = new();
    private string? _routerAddress;

    /// <summary>
    /// Set the router address. When set, AddGroupTrust also registers
    /// Router→token trust (mirrors Hub.sol: Router must trust collateral).
    /// </summary>
    public void SetRouterAddress(string routerAddress) =>
        _routerAddress = routerAddress.ToLowerInvariant();

    /// <summary>
    /// Add a trust relationship using integer node IDs.
    /// </summary>
    public void AddTrust(int trusterId, int trusteeId)
    {
        _trusts.Add((AddressIdPool.StringOf(trusterId), AddressIdPool.StringOf(trusteeId), 100));
    }

    /// <summary>
    /// Add a trust relationship using address strings.
    /// </summary>
    public void AddTrust(string truster, string trustee, int limit = 100)
    {
        _trusts.Add((truster.ToLowerInvariant(), trustee.ToLowerInvariant(), limit));
    }

    /// <summary>
    /// Add a balance entry using integer node IDs.
    /// Amount is in 6-decimal truncated form (e.g., 200_000_000 = 200 CRC).
    /// </summary>
    public void AddBalance(int holderId, int tokenId, long amount, bool isWrapped = false, bool isStatic = false)
    {
        // V2BalanceGraph expects amounts in WEI (18 decimals) and truncates by 10^12
        // So we need to multiply our truncated amount by 10^12 to get back to WEI
        // e.g., 200_000_000 (200 CRC truncated) → "200000000000000000000" WEI
        var weiAmount = new UInt256((ulong)amount) * new UInt256(1_000_000_000_000);
        _balances.Add((weiAmount.ToString(), holderId, tokenId, isWrapped, isStatic));
    }

    /// <summary>
    /// Add a balance entry using address strings and WEI amount.
    /// </summary>
    public void AddBalanceWei(string holder, string token, string amountWei, bool isWrapped = false, bool isStatic = false)
    {
        var holderId = AddressIdPool.IdOf(holder.ToLowerInvariant());
        var tokenId = AddressIdPool.IdOf(token.ToLowerInvariant());
        _balances.Add((amountWei, holderId, tokenId, isWrapped, isStatic));
    }

    /// <summary>
    /// Add a group address using integer node ID.
    /// Hub.sol: registerGroup calls _insertAvatar, so groups are registered avatars.
    /// </summary>
    public void AddGroup(int groupId)
    {
        var addr = AddressIdPool.StringOf(groupId);
        _groups.Add(addr);
        _registeredAvatars.Add(addr);
    }

    /// <summary>
    /// Add a group address using address string.
    /// Hub.sol: registerGroup calls _insertAvatar, so groups are registered avatars.
    /// </summary>
    public void AddGroup(string groupAddress)
    {
        var lower = groupAddress.ToLowerInvariant();
        _groups.Add(lower);
        _registeredAvatars.Add(lower);
    }

    /// <summary>
    /// Add a group trust relationship using integer node IDs.
    /// </summary>
    public void AddGroupTrust(int groupId, int trustedTokenId)
    {
        _groupTrusts.Add((AddressIdPool.StringOf(groupId), AddressIdPool.StringOf(trustedTokenId)));
        if (_routerAddress != null)
            _trusts.Add((_routerAddress, AddressIdPool.StringOf(trustedTokenId), 100));
    }

    /// <summary>
    /// Add a group trust relationship using address strings.
    /// </summary>
    public void AddGroupTrust(string groupAddress, string trustedToken)
    {
        _groupTrusts.Add((groupAddress.ToLowerInvariant(), trustedToken.ToLowerInvariant()));
        if (_routerAddress != null)
            _trusts.Add((_routerAddress, trustedToken.ToLowerInvariant(), 100));
    }

    /// <summary>
    /// Add a consented flow flag using integer node ID.
    /// </summary>
    public void AddConsentedAvatar(int avatarId, bool hasConsentedFlow = true)
    {
        _consentedFlags.Add((AddressIdPool.StringOf(avatarId), hasConsentedFlow));
    }

    /// <summary>
    /// Add a consented flow flag using address string.
    /// </summary>
    public void AddConsentedAvatar(string avatar, bool hasConsentedFlow = true)
    {
        _consentedFlags.Add((avatar.ToLowerInvariant(), hasConsentedFlow));
    }

    // ILoadGraph implementation

    public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
    {
        return _trusts;
    }

    public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> LoadV2Balances()
    {
        return _balances;
    }

    public IEnumerable<string> LoadGroups()
    {
        return _groups;
    }

    public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts()
    {
        return _groupTrusts;
    }

    public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags()
    {
        return _consentedFlags;
    }

    public IEnumerable<string> LoadRegisteredAvatars()
    {
        return _registeredAvatars;
    }

    public IEnumerable<(string WrapperAddress, string UnderlyingAvatar)> LoadWrapperMappings()
    {
        return _wrapperMappings;
    }

    /// <summary>
    /// Register an avatar address so it appears in LoadRegisteredAvatars().
    /// </summary>
    public void AddRegisteredAvatar(string address)
    {
        _registeredAvatars.Add(address.ToLowerInvariant());
    }

    /// <summary>
    /// Add a wrapper→avatar mapping for testing.
    /// </summary>
    public void AddWrapperMapping(string wrapperAddress, string underlyingAvatar)
    {
        _wrapperMappings.Add((wrapperAddress.ToLowerInvariant(), underlyingAvatar.ToLowerInvariant()));
    }

    /// <summary>
    /// Clears all data for reuse.
    /// </summary>
    public void Clear()
    {
        _trusts.Clear();
        _balances.Clear();
        _groups.Clear();
        _groupTrusts.Clear();
        _consentedFlags.Clear();
        _registeredAvatars.Clear();
        _wrapperMappings.Clear();
    }

    /// <summary>
    /// Gets statistics about the current mock data.
    /// </summary>
    public (int TrustCount, int BalanceCount, int GroupCount, int GroupTrustCount, int ConsentedCount) GetStats()
    {
        return (_trusts.Count, _balances.Count, _groups.Count, _groupTrusts.Count, _consentedFlags.Count);
    }
}
