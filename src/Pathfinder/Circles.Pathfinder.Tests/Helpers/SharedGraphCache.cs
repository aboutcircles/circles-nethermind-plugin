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
/// </summary>
public static class SharedGraphCache
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    private static readonly ConcurrentDictionary<long, Lazy<CachedGraphData>> Cache = new();
    private static readonly ConcurrentDictionary<long, TestEnvironmentClient> Sessions = new();

    /// <summary>
    /// Gets (or creates) cached graph data for the given block number.
    /// First call for a block creates a session and loads all raw ILoadGraph data.
    /// Subsequent calls return the cached data immediately.
    /// </summary>
    public static CachedGraphData GetOrLoad(long blockNumber)
    {
        var lazy = Cache.GetOrAdd(blockNumber, block => new Lazy<CachedGraphData>(() =>
        {
            Console.WriteLine($"[SharedGraphCache] Loading graph data for block {block}...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var session = CreateSession(block);
            var settings = new Settings();

            ILoadGraph sourceLoader = session.IsDirectConnectionAvailable
                ? new LoadGraph(session.PostgresConnectionString!, settings)
                : new ProxyLoadGraph(session, settings);

            // Materialize all data into memory (the expensive network calls)
            var balances = sourceLoader.LoadV2Balances().ToList();
            var trust = sourceLoader.LoadV2Trust().ToList();
            var groups = sourceLoader.LoadGroups().ToList();
            var groupTrusts = sourceLoader.LoadGroupTrusts().ToList();
            var consentedFlags = sourceLoader.LoadConsentedFlowFlags().ToList();

            sw.Stop();
            Console.WriteLine($"[SharedGraphCache] Block {block}: " +
                $"{trust.Count} trust edges, " +
                $"{balances.Count} balances, " +
                $"{groups.Count} groups, " +
                $"{consentedFlags.Count(f => f.HasConsentedFlow)} consented — " +
                $"loaded in {sw.ElapsedMilliseconds}ms");

            return new CachedGraphData
            {
                BlockNumber = block,
                Balances = balances,
                Trust = trust,
                Groups = groups,
                GroupTrusts = groupTrusts,
                ConsentedFlags = consentedFlags,
                Session = session
            };
        }));

        return lazy.Value;
    }

    /// <summary>
    /// Creates a GraphFactory backed by cached data. No network I/O.
    /// Each call returns a fresh factory that can build CapacityGraphs for different requests.
    /// </summary>
    public static GraphFactory CreateFactory(long blockNumber)
    {
        var data = GetOrLoad(blockNumber);
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
        return Cache.TryGetValue(blockNumber, out var lazy) && lazy.IsValueCreated;
    }

    /// <summary>
    /// Clears all cached data and disposes sessions.
    /// Call from [OneTimeTearDown] in test fixtures.
    /// </summary>
    public static async Task ClearAsync()
    {
        foreach (var kvp in Sessions)
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
        Sessions.Clear();
        Cache.Clear();
    }

    private static TestEnvironmentClient CreateSession(long blockNumber)
    {
        return Sessions.GetOrAdd(blockNumber, block =>
        {
            var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
            return TestEnvironmentClient.CreateSessionAsync(
                block,
                features: ["db"],
                ttl: "15m",
                testEnvUrl: testEnvUrl
            ).GetAwaiter().GetResult();
        });
    }
}

/// <summary>
/// Cached raw ILoadGraph results for a specific block number.
/// All data is materialized in memory — replaying it requires zero network I/O.
/// </summary>
public class CachedGraphData
{
    public long BlockNumber { get; init; }
    public List<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> Balances { get; init; } = [];
    public List<(string Truster, string Trustee, int Limit)> Trust { get; init; } = [];
    public List<string> Groups { get; init; } = [];
    public List<(string GroupAddress, string TrustedToken)> GroupTrusts { get; init; } = [];
    public List<(string Avatar, bool HasConsentedFlow)> ConsentedFlags { get; init; } = [];
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

    public IEnumerable<(string GroupAddress, string TrustedToken)>
        LoadGroupTrusts() => data.GroupTrusts;

    public IEnumerable<(string Avatar, bool HasConsentedFlow)>
        LoadConsentedFlowFlags() => data.ConsentedFlags;
}
