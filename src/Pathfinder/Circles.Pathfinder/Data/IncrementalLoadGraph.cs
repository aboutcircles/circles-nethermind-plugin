using System.Globalization;
using System.Linq;
using System.Numerics;
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
            var (inflationaryBalance, lastActivity) = kv.Value;

            // Filter: must be a registered avatar
            if (!_avatarState.Contains(account)) continue;

            // All in-memory balances are stored as inflationary (type="demurraged")
            var adjusted = DemurrageCalculator.Apply(
                inflationaryBalance, lastActivity, isStatic: false, ctx,
                _logger, account.Length >= 10 ? account[..10] : account);

            if (adjusted == null) continue;

            results.Add((adjusted.Value.ToString(CultureInfo.InvariantCulture),
                AddressIdPool.IdOf(account.ToLowerInvariant()),
                AddressIdPool.IdOf(tokenAddress.ToLowerInvariant()),
                false,  // isWrapped: full-state query doesn't load wrapped balances
                false));  // isStatic: in-memory stores inflationary balances (type="demurraged")
        }

        return results;
    }

    public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
    {
        return _trustState.GetActiveTrusts(
            _maxBlockTimestamp,
            _avatarState.AvatarSet,
            _avatarState.GetGroupSet());
    }

    /// <summary>
    /// Delegates to inner LoadGraph — groups table is small (~100 rows), fast query.
    /// </summary>
    public IEnumerable<string> LoadGroups() => _inner.LoadGroups();

    /// <summary>
    /// Derives group trusts from in-memory trust state — avoids the slow V_CrcV2_TrustRelations view.
    /// Uses the router-filtered group set from LoadGroups() (not the full avatar group set)
    /// to match the DB-backed groupTrustQuery.sql which filters by mint policy.
    /// </summary>
    public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts()
    {
        // Use router-filtered groups from DB (matches groupTrustQuery.sql's WHERE mint = '0xCDFc...')
        var routerGroups = new HashSet<string>(_inner.LoadGroups().Select(g => g.ToLowerInvariant()));
        return _trustState.GetGroupTrusts(routerGroups, _maxBlockTimestamp);
    }

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
        foreach (var avatar in _avatarState.AvatarSet)
            yield return avatar;
    }
}
