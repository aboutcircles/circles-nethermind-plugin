using System.Collections.Concurrent;
using System.Diagnostics;
using Circles.Common;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host.Data;
using Npgsql;

namespace Circles.Pathfinder.Host.State;

/// <summary>
/// LRU cache of block-filtered graph data for historical pathfinding.
/// When a request arrives with X-Max-Block-Number, the pathfinder loads graph data
/// at that block (using block-filtered SQL) and caches the materialized results.
///
/// Thread-safe. Each cached entry holds materialized trust/balance/group data (~100-200MB,
/// network-dependent). Max entries configurable (default 5, budget ~0.5-1GB).
/// Blockchain data at block N is immutable, so cached graphs never become stale.
///
/// A dedicated load semaphore (default 2 concurrent loads) prevents historical
/// graph loading from starving live pathfinding requests.
/// </summary>
public sealed class HistoricalGraphCache
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly Settings _settings;
    private readonly string _routerAddress;
    private readonly ILogger<HistoricalGraphCache> _logger;
    private readonly int _maxEntries;

    private readonly ConcurrentDictionary<long, CachedHistoricalGraph> _cache = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _loadLocks = new();

    // Serializes capacity-check + publish so concurrent loads of different blocks
    // can't both observe free capacity, both skip eviction, and both insert past _maxEntries.
    private readonly object _cacheMutationGate = new();

    /// <summary>
    /// Limits concurrent historical graph loads to prevent DB/memory exhaustion.
    /// Separate from the pathfinder's request semaphore.
    /// </summary>
    private readonly SemaphoreSlim _globalLoadSemaphore;

    // Test hooks (InternalsVisibleTo: Circles.Pathfinder.Tests): override the DB-backed
    // load and observe cache contents without a database.
    internal Func<long, GraphFactory>? LoadGraphOverride { get; init; }
    internal IReadOnlyCollection<long> CachedBlockNumbers => _cache.Keys.ToArray();

    public HistoricalGraphCache(
        NpgsqlDataSource dataSource,
        Settings settings,
        ILogger<HistoricalGraphCache> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(settings);

        _dataSource = dataSource;
        _settings = settings;
        _routerAddress = settings.RouterAddress;
        _logger = logger;

        _maxEntries = Math.Max(1, int.TryParse(
            Environment.GetEnvironmentVariable("HISTORICAL_GRAPH_CACHE_MAX_ENTRIES"),
            out var max) ? max : 5);

        var maxConcurrentLoads = Math.Max(1, int.TryParse(
            Environment.GetEnvironmentVariable("HISTORICAL_MAX_CONCURRENT_LOADS"),
            out var loads) ? loads : 2);
        _globalLoadSemaphore = new SemaphoreSlim(maxConcurrentLoads, maxConcurrentLoads);
    }

    /// <summary>
    /// Gets (or loads) a GraphFactory for the given block number.
    /// The factory is backed by cached materialized data — no DB I/O on cache hit.
    /// </summary>
    public async Task<GraphFactory> GetOrLoadFactoryAsync(long blockNumber)
    {
        // Fast path: already cached
        if (_cache.TryGetValue(blockNumber, out var cached))
        {
            Interlocked.Exchange(ref cached.LastAccessedTicks, DateTimeOffset.UtcNow.Ticks);
            return cached.Factory;
        }

        // Serialize loading per block to prevent duplicate loads
        var loadLock = _loadLocks.GetOrAdd(blockNumber, _ => new SemaphoreSlim(1, 1));
        await loadLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_cache.TryGetValue(blockNumber, out cached))
            {
                Interlocked.Exchange(ref cached.LastAccessedTicks, DateTimeOffset.UtcNow.Ticks);
                return cached.Factory;
            }

            // Acquire global load semaphore to limit concurrent DB pressure
            await _globalLoadSemaphore.WaitAsync();
            GraphFactory factory;
            try
            {
                factory = await Task.Run(() =>
                    LoadGraphOverride != null ? LoadGraphOverride(blockNumber) : LoadGraph(blockNumber));
            }
            finally
            {
                _globalLoadSemaphore.Release();
            }

            // Atomic capacity-check + publish: holding _cacheMutationGate ensures
            // two concurrent loads of different blocks can't both bypass eviction
            // and grow the cache past _maxEntries.
            lock (_cacheMutationGate)
            {
                var evictionAttempts = 0;
                while (_cache.Count >= _maxEntries && evictionAttempts++ < _maxEntries + 1)
                {
                    EvictOldest();
                }

                _cache[blockNumber] = new CachedHistoricalGraph
                {
                    Factory = factory,
                    LastAccessedTicks = DateTimeOffset.UtcNow.Ticks
                };
            }

            return factory;
        }
        finally
        {
            loadLock.Release();
        }
    }

    private GraphFactory LoadGraph(long blockNumber)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Loading historical graph for block {Block}...", blockNumber);

        // Copy all relevant settings for consistent behavior between live and historical.
        // Score-group settings MUST be copied too — otherwise the historical score loaders
        // (LoadGroupRouters/LoadScoreRouters/LoadScoreGroupMintLimits) see empty policy lists
        // and silently degrade every score group to a regular group.
        var historicalSettings = new Settings
        {
            StandardMintPolicyAddress = _settings.StandardMintPolicyAddress,
            GroupRouterAddress = _settings.GroupRouterAddress,
            ScoreGroupMintPolicies = _settings.ScoreGroupMintPolicies,
            ScoreTreasurySubTreasuries = _settings.ScoreTreasurySubTreasuries,
            ExcludeConsentedIntermediaries = _settings.ExcludeConsentedIntermediaries,
            DisableConsentedFlow = _settings.DisableConsentedFlow,
            DemurrageSafetyMargin = 1.0 // No safety margin needed for historical (deterministic timestamp)
        };

        var loader = new HistoricalLoadGraph(_dataSource, blockNumber, historicalSettings,
            _logger as ILogger);

        // Look up the block's timestamp for deterministic demurrage
        var blockTimestamp = loader.GetBlockTimestamp();
        if (blockTimestamp <= 0)
        {
            throw new ArgumentException(
                $"Block {blockNumber} not found in System_Block table — cannot load historical graph. " +
                "Ensure the block number is valid and has been indexed.");
        }

        historicalSettings.TargetDemurrageTimestamp =
            DateTimeOffset.FromUnixTimeSeconds(blockTimestamp);

        // Materialize all graph data into memory (the expensive part — subsequent uses are free)
        var balances = loader.LoadV2Balances().ToList();
        var trust = loader.LoadV2Trust().ToList();
        var groups = loader.LoadGroups().ToList();
        var organizations = loader.LoadOrganizations().ToList();
        var groupTrusts = loader.LoadGroupTrusts().ToList();
        var consentedFlags = loader.LoadConsentedFlowFlags().ToList();
        var registeredAvatars = loader.LoadRegisteredAvatars().ToList();
        var wrapperMappings = loader.LoadWrapperMappings().ToList();

        // Score-group features — without these the materialized graph treats every score
        // group as a regular group (no operator gate, unbounded mint edges), diverging from
        // the live graph. Operator approvals are queried by GraphFactory for the group
        // routers, so pre-materialize for exactly that account set.
        var groupRouters = loader.LoadGroupRouters().ToList();
        var scoreRouters = loader.LoadScoreRouters().ToList();
        var scoreGroupMintLimits = loader.LoadScoreGroupMintLimits().ToList();
        var routerAddresses = groupRouters
            .Select(r => r.RouterAddress)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();
        var operatorApprovals = loader.LoadOperatorApprovals(routerAddresses).ToList();

        // Create a factory backed by the materialized data (zero I/O on use)
        var cachedLoader = new MaterializedLoadGraph(
            balances, trust, groups, organizations, groupTrusts,
            consentedFlags, registeredAvatars, wrapperMappings,
            groupRouters, scoreRouters, scoreGroupMintLimits, operatorApprovals);

        var factory = new GraphFactory(_routerAddress, cachedLoader);

        sw.Stop();
        _logger.LogInformation(
            "Historical graph for block {Block} loaded: {Trust} trust, {Balances} balances, " +
            "{Groups} groups, {Avatars} avatars in {Elapsed}ms",
            blockNumber, trust.Count, balances.Count, groups.Count,
            registeredAvatars.Count, sw.ElapsedMilliseconds);

        return factory;
    }

    private void EvictOldest()
    {
        long oldestKey = -1;
        long oldestTicks = long.MaxValue;

        // Find oldest by iterating (safe on ConcurrentDictionary, returns snapshot)
        foreach (var kvp in _cache)
        {
            var ticks = Interlocked.Read(ref kvp.Value.LastAccessedTicks);
            if (ticks < oldestTicks)
            {
                oldestTicks = ticks;
                oldestKey = kvp.Key;
            }
        }

        if (oldestKey >= 0 && _cache.TryRemove(oldestKey, out _))
        {
            // Drop the per-block load lock entry but DO NOT dispose it: a duplicate-load
            // waiter may still hold a reference and call Release() in its finally block,
            // which would throw ObjectDisposedException. Leaving the SemaphoreSlim to GC
            // is correct — SemaphoreSlim.Dispose docs explicitly forbid disposing while
            // any thread can still access it.
            _loadLocks.TryRemove(oldestKey, out _);

            _logger.LogInformation("Evicted historical graph for block {Block}", oldestKey);
        }
    }

    private sealed class CachedHistoricalGraph
    {
        public required GraphFactory Factory { get; init; }
        public long LastAccessedTicks;
    }
}

/// <summary>
/// ILoadGraph implementation backed by pre-materialized in-memory data.
/// All methods return cached lists — zero I/O.
/// </summary>
internal sealed class MaterializedLoadGraph(
    List<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> balances,
    List<(string Truster, string Trustee, int Limit)> trust,
    List<string> groups,
    List<string> organizations,
    List<(string GroupAddress, string TrustedToken)> groupTrusts,
    List<(string Avatar, bool HasConsentedFlow)> consentedFlags,
    List<string> registeredAvatars,
    List<(string WrapperAddress, string UnderlyingAvatar, CirclesType CirclesType)> wrapperMappings,
    List<(string GroupAddress, string RouterAddress)> groupRouters,
    List<string> scoreRouters,
    List<(string GroupAddress, string CollateralToken, string AvailableLimit)> scoreGroupMintLimits,
    List<(string Account, string Operator)> operatorApprovals) : ILoadGraph
{
    public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>
        LoadV2Balances() => balances;

    public IEnumerable<(string Truster, string Trustee, int Limit)>
        LoadV2Trust() => trust;

    public IEnumerable<string> LoadGroups() => groups;
    public IEnumerable<string> LoadOrganizations() => organizations;

    public IEnumerable<(string GroupAddress, string TrustedToken)>
        LoadGroupTrusts() => groupTrusts;

    public IEnumerable<(string Avatar, bool HasConsentedFlow)>
        LoadConsentedFlowFlags() => consentedFlags;

    public IEnumerable<string> LoadRegisteredAvatars() => registeredAvatars;

    public IEnumerable<(string WrapperAddress, string UnderlyingAvatar, CirclesType CirclesType)>
        LoadWrapperMappings() => wrapperMappings;

    public IEnumerable<(string GroupAddress, string RouterAddress)> LoadGroupRouters() => groupRouters;

    public IEnumerable<string> LoadScoreRouters() => scoreRouters;

    public IEnumerable<(string GroupAddress, string CollateralToken, string AvailableLimit)>
        LoadScoreGroupMintLimits() => scoreGroupMintLimits;

    // Operator approvals were pre-materialized for the group routers (the only accounts
    // GraphFactory queries). Filter to the requested accounts to match the live semantics.
    public IEnumerable<(string Account, string Operator)> LoadOperatorApprovals(IEnumerable<string> accounts)
    {
        var accountSet = accounts
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.ToLowerInvariant())
            .ToHashSet();
        return accountSet.Count == 0
            ? []
            : operatorApprovals.Where(a => accountSet.Contains(a.Account.ToLowerInvariant())).ToList();
    }
}
