using System.Collections.Concurrent;
using Circles.Common.TestUtils;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Tests.Helpers;

/// <summary>
/// Caches loaded graph data per block number to avoid re-downloading
/// 760K+ rows from the test-env for every scenario at the same block.
///
/// Thread-safe. Lazily initialized on first access per block.
///
/// With 4 distinct blocks across 62 scenarios, this reduces total
/// data transfer from ~47M rows to ~3M rows (94% reduction).
///
/// When an external session is registered (e.g. from SharedAnvilCache),
/// it is reused instead of creating a duplicate DB session.
/// </summary>
public static class SharedGraphCache
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";
    private const int MaxRetries = 3;
    private const int RetryBaseDelayMs = 3000;

    // Successful loads — only populated after data is fully loaded
    private static readonly ConcurrentDictionary<long, CachedGraphData> Cache = new();

    // External sessions registered by other caches (e.g. SharedAnvilCache)
    private static readonly ConcurrentDictionary<long, TestEnvironmentClient> ExternalSessions = new();

    // Sessions we created ourselves (need to dispose)
    private static readonly ConcurrentDictionary<long, TestEnvironmentClient> OwnedSessions = new();

    // Lock per block to prevent concurrent loading for the same block
    private static readonly ConcurrentDictionary<long, object> LoadLocks = new();

    /// <summary>
    /// Registers an external session for a block number. Graph loading
    /// will reuse this session instead of creating a new one.
    /// Call this BEFORE GetOrLoad for best results.
    /// </summary>
    public static void RegisterSession(long blockNumber, TestEnvironmentClient session)
    {
        ExternalSessions[blockNumber] = session;
    }

    /// <summary>
    /// Gets (or creates) cached graph data for the given block number.
    /// Retries on transient DB errors (57P01, connection reset).
    /// Does NOT cache failures — next call will retry.
    ///
    /// When targetTimestamp is provided, demurrage is calculated relative to that time
    /// instead of DateTimeOffset.UtcNow. If cached data exists with a different timestamp,
    /// the cache entry is replaced with freshly loaded data.
    /// </summary>
    public static CachedGraphData GetOrLoad(long blockNumber, DateTimeOffset? targetTimestamp = null)
    {
        // Fast path: already loaded with matching timestamp
        if (Cache.TryGetValue(blockNumber, out var cached) && cached.TargetTimestamp == targetTimestamp)
            return cached;

        var lockObj = LoadLocks.GetOrAdd(blockNumber, _ => new object());
        lock (lockObj)
        {
            // Double-check after acquiring lock
            if (Cache.TryGetValue(blockNumber, out cached) && cached.TargetTimestamp == targetTimestamp)
                return cached;

            cached = LoadWithRetry(blockNumber, targetTimestamp);
            Cache[blockNumber] = cached;
            return cached;
        }
    }

    /// <summary>
    /// Creates a GraphFactory backed by cached data. No network I/O.
    /// Each call returns a fresh factory that can build CapacityGraphs for different requests.
    ///
    /// When targetTimestamp is provided, demurrage is calculated at that point in time.
    /// Use this for Anvil differential tests where the fork has a specific block timestamp.
    /// </summary>
    public static GraphFactory CreateFactory(long blockNumber, DateTimeOffset? targetTimestamp = null)
    {
        var data = GetOrLoad(blockNumber, targetTimestamp);
        return new GraphFactory(RouterAddress, data.CreateLoadGraph());
    }

    /// <summary>
    /// Gets the session for a block (creating one if needed).
    /// Useful for Anvil/RPC access beyond just the graph.
    /// </summary>
    public static TestEnvironmentClient GetSession(long blockNumber)
    {
        var data = GetOrLoad(blockNumber);
        return data.Session;
    }

    /// <summary>
    /// Check if graph data is cached for a block.
    /// </summary>
    public static bool IsCached(long blockNumber)
    {
        return Cache.ContainsKey(blockNumber);
    }

    /// <summary>
    /// Clears all cached data and disposes sessions we own.
    /// Does NOT dispose external sessions (caller manages those).
    /// Call from [OneTimeTearDown] in test fixtures.
    /// </summary>
    public static async Task ClearAsync()
    {
        foreach (var kvp in OwnedSessions)
        {
            try
            {
                await kvp.Value.DisposeAsync();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        OwnedSessions.Clear();
        ExternalSessions.Clear();
        Cache.Clear();
        LoadLocks.Clear();
    }

    private static CachedGraphData LoadWithRetry(long blockNumber, DateTimeOffset? targetTimestamp)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return LoadGraphData(blockNumber, targetTimestamp);
            }
            catch (Exception ex) when (
                attempt < MaxRetries &&
                (ex is HttpRequestException hre &&
                    (hre.Message.Contains("57P01") ||
                     hre.Message.Contains("57P03") ||
                     hre.Message.Contains("08006") ||
                     hre.Message.Contains("connection") && hre.Message.Contains("reset")) ||
                 ex is TaskCanceledException))
            {
                lastException = ex;
                var delay = RetryBaseDelayMs * attempt;
                Console.WriteLine($"[SharedGraphCache] Block {blockNumber}: transient DB error " +
                    $"on attempt {attempt}/{MaxRetries} ({GetSqlStateHint(ex.Message)}), " +
                    $"retrying in {delay}ms...");
                Thread.Sleep(delay);

                // If we're using our own session, it might be dead — create a fresh one
                if (OwnedSessions.TryRemove(blockNumber, out var deadSession))
                {
                    try { deadSession.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    catch { /* ignore */ }
                }
            }
        }

        throw new InvalidOperationException(
            $"Failed to load graph data for block {blockNumber} after {MaxRetries} attempts",
            lastException);
    }

    private static CachedGraphData LoadGraphData(long blockNumber, DateTimeOffset? targetTimestamp)
    {
        Console.WriteLine($"[SharedGraphCache] Loading graph data for block {blockNumber}" +
            (targetTimestamp.HasValue ? $" (demurrage at {targetTimestamp.Value:u})" : " (demurrage at UtcNow)") +
            "...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var session = GetOrCreateSession(blockNumber);
        var settings = new Settings { TargetDemurrageTimestamp = targetTimestamp };

        ILoadGraph sourceLoader = session.IsDirectConnectionAvailable
            ? new LoadGraph(session.PostgresConnectionString!, settings)
            : new ProxyLoadGraph(session, settings);

        // Materialize all data into memory (the expensive network calls)
        var balances = sourceLoader.LoadV2Balances().ToList();
        var trust = sourceLoader.LoadV2Trust().ToList();
        var groups = sourceLoader.LoadGroups().ToList();
        var organizations = sourceLoader.LoadOrganizations().ToList();
        var groupTrusts = sourceLoader.LoadGroupTrusts().ToList();
        var consentedFlags = sourceLoader.LoadConsentedFlowFlags().ToList();
        var registeredAvatars = sourceLoader.LoadRegisteredAvatars().ToList();

        sw.Stop();
        Console.WriteLine($"[SharedGraphCache] Block {blockNumber}: " +
            $"{trust.Count} trust edges, " +
            $"{balances.Count} balances, " +
            $"{groups.Count} groups, " +
            $"{registeredAvatars.Count} avatars, " +
            $"{consentedFlags.Count(f => f.HasConsentedFlow)} consented — " +
            $"loaded in {sw.ElapsedMilliseconds}ms");

        return new CachedGraphData
        {
            BlockNumber = blockNumber,
            TargetTimestamp = targetTimestamp,
            Balances = balances,
            Trust = trust,
            Groups = groups,
            Organizations = organizations,
            GroupTrusts = groupTrusts,
            ConsentedFlags = consentedFlags,
            RegisteredAvatars = registeredAvatars,
            Session = session
        };
    }

    private static TestEnvironmentClient GetOrCreateSession(long blockNumber)
    {
        // Prefer external session (from SharedAnvilCache etc.)
        if (ExternalSessions.TryGetValue(blockNumber, out var external))
        {
            Console.WriteLine($"[SharedGraphCache] Reusing external session for block {blockNumber}");
            return external;
        }

        // Create our own
        return OwnedSessions.GetOrAdd(blockNumber, block =>
        {
            Console.WriteLine($"[SharedGraphCache] Creating DB session for block {block}...");
            var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
            return TestEnvironmentClient.CreateSessionAsync(
                block,
                features: ["db"],
                ttl: "30m",
                testEnvUrl: testEnvUrl
            ).GetAwaiter().GetResult();
        });
    }

    private static string GetSqlStateHint(string message)
    {
        if (message.Contains("57P01")) return "admin_shutdown";
        if (message.Contains("57P03")) return "cannot_connect_now";
        if (message.Contains("08006")) return "connection_failure";
        return "connection_reset";
    }
}

/// <summary>
/// Cached raw ILoadGraph results for a specific block number.
/// All data is materialized in memory — replaying it requires zero network I/O.
/// </summary>
public class CachedGraphData
{
    public long BlockNumber { get; init; }
    public DateTimeOffset? TargetTimestamp { get; init; }
    public List<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> Balances { get; init; } = [];
    public List<(string Truster, string Trustee, int Limit)> Trust { get; init; } = [];
    public List<string> Groups { get; init; } = [];
    public List<string> Organizations { get; init; } = [];
    public List<(string GroupAddress, string TrustedToken)> GroupTrusts { get; init; } = [];
    public List<(string Avatar, bool HasConsentedFlow)> ConsentedFlags { get; init; } = [];
    public List<string> RegisteredAvatars { get; init; } = [];
    public TestEnvironmentClient Session { get; init; } = null!;

    /// <summary>
    /// Creates an ILoadGraph that replays the cached raw data.
    /// </summary>
    public ILoadGraph CreateLoadGraph() => new CachedLoadGraph(this);
}

/// <summary>
/// ILoadGraph implementation that replays cached raw data. Zero I/O.
/// Faithfully reproduces all 5 data sources (balances, trust, groups, group trusts, consented flags).
/// </summary>
internal class CachedLoadGraph(CachedGraphData data) : ILoadGraph
{
    public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>
        LoadV2Balances() => data.Balances;

    public IEnumerable<(string Truster, string Trustee, int Limit)>
        LoadV2Trust() => data.Trust;

    public IEnumerable<string>
        LoadGroups() => data.Groups;

    public IEnumerable<string> LoadOrganizations() => data.Organizations;

    public IEnumerable<(string GroupAddress, string TrustedToken)>
        LoadGroupTrusts() => data.GroupTrusts;

    public IEnumerable<(string Avatar, bool HasConsentedFlow)>
        LoadConsentedFlowFlags() => data.ConsentedFlags;

    public IEnumerable<string>
        LoadRegisteredAvatars() => data.RegisteredAvatars;

    public IEnumerable<(string WrapperAddress, string UnderlyingAvatar, int CirclesType)>
        LoadWrapperMappings() => Array.Empty<(string, string, int)>();
}
