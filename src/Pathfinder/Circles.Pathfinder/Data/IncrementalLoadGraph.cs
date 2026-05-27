using System.Globalization;
using System.Linq;
using Circles.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Circles.Pathfinder.Data;

/// <summary>
/// ILoadGraph implementation backed by in-memory state stores.
/// Reads balances/trusts from InMemoryBalanceState/InMemoryTrustState,
/// delegates small-table queries (groups, consented flow) to the inner LoadGraph.
/// Uses <see cref="DemurrageCalculator"/> for demurrage — same code path as LoadGraph.
/// </summary>
public class IncrementalLoadGraph : ILoadGraph
{
    private readonly InMemoryBalanceState _balanceState;
    private readonly InMemoryTrustState _trustState;
    private readonly InMemoryAvatarState _avatarState;
    private readonly ILoadGraph _inner;
    private readonly Settings _settings;
    private readonly long _maxBlockTimestamp;
    private readonly ILogger _logger;
    private IReadOnlyList<(string WrapperAddress, string UnderlyingAvatar, int CirclesType)>? _cachedWrapperMappings;

    public IncrementalLoadGraph(
        InMemoryBalanceState balanceState,
        InMemoryTrustState trustState,
        InMemoryAvatarState avatarState,
        ILoadGraph inner,
        Settings settings,
        long maxBlockTimestamp,
        ILogger? logger = null)
    {
        _balanceState = balanceState;
        _trustState = trustState;
        _avatarState = avatarState;
        _inner = inner;
        _settings = settings;
        _maxBlockTimestamp = maxBlockTimestamp;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Produce balance tuples from in-memory state with demurrage applied.
    /// Uses shared <see cref="DemurrageCalculator"/> — same code path as LoadGraph.
    /// Filters to registered avatars, applies demurrage + safety margin, skips zeros.
    /// </summary>
    public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>
        LoadV2Balances()
    {
        var results = new List<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>();
        var ctx = DemurrageCalculator.CreateContext(_settings);

        foreach (var kv in _balanceState.GetAll())
        {
            var (account, tokenAddress) = kv.Key;
            var (rawBalance, lastActivity, isWrapped, isStatic) = kv.Value;

            // Filter: holder must be active (registered + not stopped)
            if (!_avatarState.Contains(account)) continue;
            // Token owner must be registered (stopped is OK — their tokens are still valid on-chain)
            if (!isWrapped && !_avatarState.IsRegistered(tokenAddress)) continue;

            var adjusted = DemurrageCalculator.Apply(
                rawBalance, lastActivity, isStatic, ctx,
                _logger, account.Length >= 10 ? account[..10] : account);

            if (adjusted == null) continue;

            results.Add((adjusted.Value.ToString(CultureInfo.InvariantCulture),
                AddressIdPool.IdOf(account.ToLowerInvariant()),
                AddressIdPool.IdOf(tokenAddress.ToLowerInvariant()),
                isWrapped,
                isStatic));
        }

        return results;
    }

    /// <summary>
    /// Load trust edges from in-memory state, enriched with wrapper-derived trust.
    /// For each direct trust (A→B), also yields trust edges for B's ERC20 wrappers.
    /// </summary>
    public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
    {
        var activeAvatars = _avatarState.GetActiveAvatarSet();
        var groups = _avatarState.GetGroupSet();

        var directTrusts = _trustState.GetActiveTrusts(
                _maxBlockTimestamp,
                activeAvatars,
                groups)
            .ToList();

        foreach (var trust in directTrusts)
        {
            yield return trust;
        }

        var wrapperMappingsByAvatar = GetCachedWrapperMappings()
            .GroupBy(x => x.UnderlyingAvatar.ToLowerInvariant())
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.WrapperAddress.ToLowerInvariant()).Distinct().ToArray());
        // CirclesType is intentionally dropped here — this map drives trust-edge expansion
        // (wrappers inherit the underlying avatar's trust), which is type-agnostic. The
        // CapacityGraph.InflationaryWrappers set carries the type info separately.

        foreach (var (truster, trustee, limit) in directTrusts)
        {
            if (!wrapperMappingsByAvatar.TryGetValue(trustee.ToLowerInvariant(), out var wrappers))
            {
                continue;
            }

            foreach (var wrapper in wrappers)
            {
                yield return (truster, wrapper, limit);
            }
        }
    }

    /// <summary>
    /// Delegates to inner LoadGraph — groups table is small (~100 rows), fast query.
    /// </summary>
    public IEnumerable<string> LoadGroups() => _inner.LoadGroups();

    /// <summary>
    /// Delegates to inner LoadGraph — organizations table is small.
    /// </summary>
    public IEnumerable<string> LoadOrganizations() => _inner.LoadOrganizations();

    /// <summary>
    /// Derives group trusts from in-memory trust state — avoids the slow V_CrcV2_TrustRelations view.
    /// Uses the router-filtered group set from LoadGroups() (not the full avatar group set)
    /// to match the DB-backed groupTrustQuery.sql which filters by mint policy.
    /// </summary>
    public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts()
    {
        // Groups filtered by standard mint policy from DB (matches groupQuery.sql parameterized by StandardMintPolicyAddress)
        var routerGroups = new HashSet<string>(_inner.LoadGroups().Select(g => g.ToLowerInvariant()));
        // Use AvatarSet (includes stopped) — stopped avatars' tokens are still valid on-chain
        return _trustState.GetGroupTrusts(routerGroups, _maxBlockTimestamp, _avatarState.AvatarSet);
    }

    public IEnumerable<(string GroupAddress, string RouterAddress)> LoadGroupRouters()
        => _inner.LoadGroupRouters();

    public IEnumerable<(string GroupAddress, string CollateralToken, string AvailableLimit)> LoadScoreGroupMintLimits()
        => _inner.LoadScoreGroupMintLimits();

    /// <summary>
    /// Score-router set + ERC-1155 operator approvals are sourced from index tables that
    /// the in-memory state doesn't shadow, so delegate to the inner DB-backed loader.
    /// Without these overrides every base-snapshot build would inherit the
    /// ILoadGraph default-empty implementations and silently disable the score-router
    /// approval gate in production.
    /// </summary>
    public IEnumerable<string> LoadScoreRouters()
        => _inner.LoadScoreRouters();

    public IEnumerable<(string Account, string Operator)> LoadOperatorApprovals(IEnumerable<string> accounts)
        => _inner.LoadOperatorApprovals(accounts);

    /// <summary>
    /// Delegates to inner LoadGraph — consented flow table is small, fast query.
    /// </summary>
    public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags()
        => _inner.LoadConsentedFlowFlags();

    /// <summary>
    /// Returns all registered, non-stopped avatars from in-memory state.
    /// </summary>
    public IEnumerable<string> LoadRegisteredAvatars()
    {
        foreach (var avatar in _avatarState.GetActiveAvatarSet())
            yield return avatar;
    }

    /// <summary>
    /// Returns cached wrapper mappings — loaded once per graph build from inner LoadGraph.
    /// Wrapper deployments are append-only, so caching within a single build is safe.
    /// </summary>
    public IEnumerable<(string WrapperAddress, string UnderlyingAvatar, int CirclesType)> LoadWrapperMappings()
        => GetCachedWrapperMappings();

    private IReadOnlyList<(string WrapperAddress, string UnderlyingAvatar, int CirclesType)> GetCachedWrapperMappings()
        => _cachedWrapperMappings ??= _inner.LoadWrapperMappings().ToList();
}
