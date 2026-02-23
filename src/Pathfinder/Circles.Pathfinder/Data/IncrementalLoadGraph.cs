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
/// Demurrage logic is replicated exactly from LoadGraph.LoadV2Balances().
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

    // Demurrage constants — must match LoadGraph exactly
    private const uint InflationDayZeroUnix = 1_675_209_600; // Feb 1, 2023 00:00 UTC
    private const ulong SecondsPerDay = 86_400;

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
    /// Replicates LoadGraph.LoadV2Balances() logic exactly:
    ///   1. Calculate targetDay from settings
    ///   2. Filter to registered avatars only
    ///   3. Guard against pre-epoch lastActivity
    ///   4. Apply demurrage via CirclesConverter.InflationaryToDemurrage
    ///   5. Apply safety margin in live mode
    ///   6. Skip zeros
    /// </summary>
    public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>
        LoadV2Balances()
    {
        var results = new List<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>();

        // Calculate target day for demurrage (configurable for testing, defaults to NOW)
        var targetTimestamp = _settings.TargetDemurrageTimestamp ?? DateTimeOffset.UtcNow;
        var targetDay = CirclesConverter.DayFromTimestamp(targetTimestamp, InflationDayZeroUnix);

        // Safety margin: only in live mode (no frozen timestamp)
        bool applyMargin = _settings.TargetDemurrageTimestamp == null
                           && _settings.DemurrageSafetyMargin < 1.0;

        foreach (var kv in _balanceState.GetAll())
        {
            var (account, tokenAddress) = kv.Key;
            var (inflationaryBalance, lastActivity) = kv.Value;

            // Filter: must be a registered avatar
            if (!_avatarState.Contains(account)) continue;

            // Guard: corrupted data where lastActivity predates Circles epoch
            if (lastActivity < InflationDayZeroUnix)
            {
                _logger.LogWarning(
                    "[IncrementalLoadGraph] lastActivity {LastActivity} < InflationDayZero {Epoch} for account={Account}, token={Token} — skipping",
                    lastActivity, InflationDayZeroUnix,
                    account.Length >= 10 ? account[..10] : account,
                    tokenAddress.Length >= 10 ? tokenAddress[..10] : tokenAddress);
                continue;
            }

            // Apply demurrage from lastActivity to target timestamp
            // All in-memory balances are stored as inflationary (type="demurraged" in original query)
            var lastActivityDay = (ulong)(lastActivity - InflationDayZeroUnix) / SecondsPerDay;
            var daysDelta = targetDay > lastActivityDay ? targetDay - lastActivityDay : 0;

            string balance;
            if (daysDelta > 0)
            {
                var demurragedBalance = CirclesConverter.InflationaryToDemurrage(inflationaryBalance, daysDelta);
                balance = demurragedBalance.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                balance = inflationaryBalance.ToString(CultureInfo.InvariantCulture);
            }

            // Apply safety margin in live mode
            if (applyMargin && balance != "0")
            {
                var raw = BigInteger.Parse(balance);
                var margined = (BigInteger)((double)raw * _settings.DemurrageSafetyMargin);
                balance = margined.ToString(CultureInfo.InvariantCulture);
            }

            if (balance == "0") continue;

            results.Add((balance,
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
}
